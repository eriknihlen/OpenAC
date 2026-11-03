//! DAT directory detection, file hashes, the install record and its
//! verification cache. Ported from OpenAC
//! `tests/AcDream.Launcher.Core.Tests/Installation/` (DatDirectoryLocatorTests,
//! LauncherInstallRecordStoreTests, PreparedAssetVerificationCacheTests) and
//! `Integrity/FileIntegrityTests.cs`.

mod common;

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use ac_host::paths::root_pointer;
use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::fsutil::full_path;
use ac_launcher_core::installation::{
    CURRENT_BAKE_TOOL_VERSION, ContentWorkKind, DatDirectoryLocator,
    InstallRecordVerificationState, InstallRootMover, LauncherInstallRecord,
    LauncherInstallRecordStore, MOVE_SESSION_REFUSAL, REQUIRED_DAT_FILE_NAMES, SameVolume,
    backup_path, repair_content_records,
};
use ac_launcher_core::integrity::{self, compute_sha256_hex, sha256_hex};
use ac_launcher_core::updates::UpdateSessionBarrier;
use common::TempDir;

fn none() -> CancelToken {
    CancelToken::none()
}

fn create_complete_dat_directory(directory: &Path) {
    std::fs::create_dir_all(directory).unwrap();
    for name in REQUIRED_DAT_FILE_NAMES {
        std::fs::write(directory.join(name), "fixture").unwrap();
    }
}

// --- DatDirectoryLocatorTests ---------------------------------------------------

#[test]
fn portable_validation_requires_the_four_exact_dat_file_names() {
    let root = TempDir::new("dats");
    let directory = root.join("retail");
    std::fs::create_dir_all(&directory).unwrap();
    for name in &REQUIRED_DAT_FILE_NAMES[..3] {
        std::fs::write(directory.join(name), "fixture").unwrap();
    }
    let locator = DatDirectoryLocator::new(false, None);
    let incomplete = locator.validate(Some(&directory));
    assert!(!incomplete.is_valid);
    assert_eq!(incomplete.missing_file_names, ["client_local_English.dat"]);

    std::fs::write(directory.join("client_local_English.dat"), "fixture").unwrap();
    let valid = locator.validate(Some(&directory));
    assert!(valid.is_valid);
    assert_eq!(valid.directory, full_path(&directory));
    assert!(valid.missing_file_names.is_empty());
}

#[test]
fn windows_detection_checks_both_conventional_locations_in_order() {
    let root = TempDir::new("dats-detect");
    let documents = root.join("Documents").join("Asheron's Call");
    let turbine = root.join("Turbine").join("Asheron's Call");
    create_complete_dat_directory(&documents);
    std::fs::create_dir_all(&turbine).unwrap();
    std::fs::write(turbine.join("client_portal.dat"), "fixture").unwrap();
    let locator = DatDirectoryLocator::new(true, Some(vec![documents.clone(), turbine.clone()]));
    let detected = locator.detect();
    assert_eq!(detected.len(), 2);
    assert_eq!(detected[0].directory, full_path(&documents));
    assert!(detected[0].is_valid);
    assert_eq!(detected[1].directory, full_path(&turbine));
    assert!(!detected[1].is_valid);
    assert_eq!(detected[1].missing_file_names.len(), 3);
}

#[test]
fn linux_has_no_windows_auto_detection_but_manual_validation_still_works() {
    let root = TempDir::new("dats-linux");
    let manual = root.join("linux-dats");
    create_complete_dat_directory(&manual);
    let locator = DatDirectoryLocator::new(false, Some(vec![manual.clone()]));
    assert!(locator.detect().is_empty());
    assert!(locator.validate(Some(&manual)).is_valid);
}

// --- FileIntegrityTests -----------------------------------------------------------

#[test]
fn file_integrity() {
    let root = TempDir::new("integrity");
    let path = root.join("file.bin");
    let content = b"acdream launcher integrity fixture";
    std::fs::write(&path, content).unwrap();
    let actual = compute_sha256_hex(&path).unwrap();
    assert_eq!(actual, sha256_hex(content));
    assert!(integrity::verify(&path, &actual).unwrap());
    assert!(integrity::verify(&path, &actual.to_uppercase()).unwrap());
    assert!(!integrity::verify(&path, &"0".repeat(64)).unwrap());

    let other = root.join("b.bin");
    std::fs::write(&other, "content B").unwrap();
    assert_ne!(compute_sha256_hex(&other).unwrap(), actual);

    let empty = root.join("empty.bin");
    std::fs::write(&empty, []).unwrap();
    assert_eq!(
        compute_sha256_hex(&empty).unwrap(),
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    );
}

// --- LauncherInstallRecordStoreTests ------------------------------------------------

struct StoreFixture {
    root: TempDir,
    dats: PathBuf,
    hashes: Arc<AtomicUsize>,
    store: LauncherInstallRecordStore,
}

impl StoreFixture {
    fn new() -> Self {
        let root = TempDir::new("install-record");
        let dats = root.join("retail-dats");
        create_complete_dat_directory(&dats);
        let hashes = Arc::new(AtomicUsize::new(0));
        let counter = Arc::clone(&hashes);
        let store = LauncherInstallRecordStore::with(
            &root.path_set(),
            DatDirectoryLocator::default(),
            move |path, cancel| {
                counter.fetch_add(1, Ordering::SeqCst);
                integrity::compute_sha256_hex_cancellable(path, cancel)
            },
        );
        Self {
            root,
            dats,
            hashes,
            store,
        }
    }

    fn write_package(&self, content: &str) {
        let path = self.store.prepared_asset_path();
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, content).unwrap();
    }

    fn record(&self) -> LauncherInstallRecord {
        let path = self.store.prepared_asset_path();
        LauncherInstallRecord::new(
            full_path(&self.dats),
            full_path(&path),
            compute_sha256_hex(&path).unwrap(),
            std::fs::metadata(&path).unwrap().len() as i64,
            CURRENT_BAKE_TOOL_VERSION,
        )
    }

    fn verify(&self) -> ac_launcher_core::installation::InstallRecordVerification {
        self.store
            .load_and_verify(&none(), false, &mut |_| {})
            .unwrap()
    }

    fn installed(content: &str) -> Self {
        let fixture = Self::new();
        fixture.write_package(content);
        fixture
            .store
            .save_atomically(&fixture.record(), &none())
            .unwrap();
        fixture
    }

    fn hash_count(&self) -> usize {
        self.hashes.load(Ordering::SeqCst)
    }

    fn write_raw_record(&self, json: &str) {
        std::fs::create_dir_all(self.store.data_directory()).unwrap();
        std::fs::write(self.store.record_path(), json).unwrap();
    }
}

fn record_json(record: &LauncherInstallRecord) -> String {
    let quote = |path: &Path| ac_launcher_core::json::quote(&path.to_string_lossy());
    format!(
        r#"{{"datDirectory":{},"preparedAssetPath":{},"preparedAssetSha256":"{}","preparedAssetSize":{},"bakeToolVersion":{},"version":1}}"#,
        quote(&record.dat_directory),
        quote(&record.prepared_asset_path),
        record.prepared_asset_sha256,
        record.prepared_asset_size,
        record.bake_tool_version
    )
}

#[test]
fn atomic_record_round_trip_verifies_sha_size_and_bake_tool_version() {
    let fixture = StoreFixture::new();
    fixture.write_package("verified package");
    let record = fixture.record();
    fixture.store.save_atomically(&record, &none()).unwrap();
    let verification = fixture.verify();
    assert!(verification.is_verified());
    assert_eq!(verification.record.as_ref(), Some(&record));
    assert!(verification.status.contains("SHA-256"));
    assert!(
        !std::fs::read_dir(fixture.store.data_directory())
            .unwrap()
            .flatten()
            .any(|entry| entry
                .file_name()
                .to_string_lossy()
                .starts_with(".install.json."))
    );
}

#[test]
fn size_and_sha_corruption_disable_the_install() {
    let fixture = StoreFixture::installed("original");
    fixture.write_package("different-size");
    let size = fixture.verify();
    assert_eq!(size.state, InstallRecordVerificationState::Invalid);
    assert!(
        size.status.to_lowercase().contains("size changed"),
        "{}",
        size.status
    );

    fixture.write_package("tampered");
    let mut same_size = fixture.record();
    same_size.prepared_asset_sha256 = "0".repeat(64);
    fixture.store.save_atomically(&same_size, &none()).unwrap();
    let sha = fixture.verify();
    assert_eq!(sha.state, InstallRecordVerificationState::Invalid);
    assert!(sha.status.contains("SHA-256"), "{}", sha.status);
}

#[test]
fn stale_bake_tool_version_prompts_for_known_migration_before_hashing() {
    let fixture = StoreFixture::new();
    fixture.write_package("package");
    let mut stale = fixture.record();
    stale.prepared_asset_sha256 = "a".repeat(64);
    stale.bake_tool_version = CURRENT_BAKE_TOOL_VERSION - 1;
    fixture.write_raw_record(&record_json(&stale));
    let verification = fixture.verify();
    assert_eq!(
        verification.state,
        InstallRecordVerificationState::ContentUpdateRequired
    );
    assert!(verification.record.is_some());
    assert_eq!(
        verification
            .required_content_work
            .as_ref()
            .map(|plan| plan.kind),
        Some(ContentWorkKind::FullRebuild)
    );
    assert!(verification.status.contains("World data update"));
    assert_eq!(fixture.hash_count(), 0);
}

#[test]
fn null_integrity_metadata_is_reported_as_invalid_instead_of_throwing() {
    let fixture = StoreFixture::new();
    let quote = |path: &Path| ac_launcher_core::json::quote(&path.to_string_lossy());
    fixture.write_raw_record(&format!(
        r#"{{"datDirectory":{},"preparedAssetPath":{},"preparedAssetSha256":null,"preparedAssetSize":12,"bakeToolVersion":{CURRENT_BAKE_TOOL_VERSION},"version":1}}"#,
        quote(&fixture.dats),
        quote(&fixture.store.prepared_asset_path())
    ));
    let verification = fixture.verify();
    assert_eq!(verification.state, InstallRecordVerificationState::Invalid);
    assert!(verification.status.to_lowercase().contains("missing"));
}

#[test]
fn missing_explicit_version_is_rejected_before_admission() {
    let fixture = StoreFixture::new();
    let quote = |path: &Path| ac_launcher_core::json::quote(&path.to_string_lossy());
    fixture.write_raw_record(&format!(
        r#"{{"datDirectory":{},"preparedAssetPath":{},"preparedAssetSha256":"{}","preparedAssetSize":12,"bakeToolVersion":{CURRENT_BAKE_TOOL_VERSION}}}"#,
        quote(&full_path(&fixture.dats)),
        quote(&fixture.store.prepared_asset_path()),
        "a".repeat(64)
    ));
    let verification = fixture.verify();
    assert_eq!(verification.state, InstallRecordVerificationState::Invalid);
    assert!(verification.status.to_lowercase().contains("explicit"));
}

#[test]
fn save_normalizes_canonical_absolute_dat_and_prepared_paths() {
    let fixture = StoreFixture::new();
    fixture.write_package("verified package");
    let prepared = fixture.store.prepared_asset_path();
    let dat_name = fixture.dats.file_name().unwrap();
    let mut non_canonical = fixture.record();
    non_canonical.dat_directory = fixture.dats.join("..").join(dat_name).join(".");
    non_canonical.prepared_asset_path = prepared
        .parent()
        .unwrap()
        .join("..")
        .join("pak")
        .join(prepared.file_name().unwrap());
    fixture
        .store
        .save_atomically(&non_canonical, &none())
        .unwrap();

    let text = std::fs::read_to_string(fixture.store.record_path()).unwrap();
    let node = ac_host::json::parse(text.as_bytes()).unwrap();
    let ac_host::json::Value::Object(members) = node.value else {
        panic!("object expected");
    };
    let member = |name: &str| {
        members
            .iter()
            .find(|member| member.name == name)
            .map(|member| member.value.value.clone())
            .unwrap()
    };
    assert_eq!(
        member("datDirectory"),
        ac_host::json::Value::String(full_path(&fixture.dats).to_string_lossy().into_owned())
    );
    assert_eq!(
        member("preparedAssetPath"),
        ac_host::json::Value::String(full_path(&prepared).to_string_lossy().into_owned())
    );
    assert_eq!(member("version"), ac_host::json::Value::Number("1".into()));
    assert!(fixture.verify().is_verified());
}

#[test]
fn a_relative_dat_record_is_refused() {
    // OpenAC changes the working directory to show the meaning would shift;
    // the refusal comes from the absolute-path rule either way, so the
    // process-wide working directory is left alone here.
    let fixture = StoreFixture::new();
    fixture.write_package("verified package");
    let mut relative = fixture.record();
    relative.dat_directory = PathBuf::from("retail-dats");
    fixture.write_raw_record(&record_json(&relative));
    let verification = fixture.verify();
    assert_eq!(verification.state, InstallRecordVerificationState::Invalid);
    assert!(
        verification.status.to_lowercase().contains("absolute"),
        "{}",
        verification.status
    );
}

#[test]
fn older_absolute_but_non_canonical_dat_document_is_rejected() {
    let fixture = StoreFixture::new();
    fixture.write_package("verified package");
    let mut non_canonical = fixture.record();
    non_canonical.dat_directory = fixture
        .dats
        .join("..")
        .join(fixture.dats.file_name().unwrap());
    fixture.write_raw_record(&record_json(&non_canonical));
    let verification = fixture.verify();
    assert_eq!(verification.state, InstallRecordVerificationState::Invalid);
    assert!(
        verification.status.to_lowercase().contains("canonical"),
        "{}",
        verification.status
    );
}

#[test]
fn startup_recovers_prior_verified_package_after_interrupted_replacement() {
    let fixture = StoreFixture::installed("previous-good");
    let prepared = fixture.store.prepared_asset_path();
    let backup = backup_path(&prepared);
    std::fs::rename(&prepared, &backup).unwrap();
    std::fs::write(&prepared, "partial-new").unwrap();
    let verification = fixture.verify();
    assert!(verification.is_verified());
    assert_eq!(std::fs::read_to_string(&prepared).unwrap(), "previous-good");
    assert!(!backup.exists());
}

// --- PreparedAssetVerificationCacheTests ---------------------------------------------

const PACKAGE: &str = "verified package";

fn set_modified(path: &Path, time: std::time::SystemTime) {
    std::fs::OpenOptions::new()
        .write(true)
        .open(path)
        .unwrap()
        .set_modified(time)
        .unwrap();
}

fn modified(path: &Path) -> std::time::SystemTime {
    std::fs::metadata(path).unwrap().modified().unwrap()
}

#[test]
fn second_startup_skips_the_hash_entirely() {
    let fixture = StoreFixture::installed(PACKAGE);
    assert!(fixture.verify().is_verified());
    assert_eq!(fixture.hash_count(), 1);
    assert!(fixture.verify().is_verified());
    assert_eq!(fixture.hash_count(), 1);
}

#[test]
fn cache_miss_reports_the_exceptional_long_read_before_hashing() {
    let fixture = StoreFixture::installed(PACKAGE);
    let mut statuses = Vec::new();
    assert!(
        fixture
            .store
            .load_and_verify(&none(), false, &mut |status| statuses
                .push(status.to_owned()))
            .unwrap()
            .is_verified()
    );
    assert!(
        statuses
            .iter()
            .any(|status| status.contains("whole world-data pak") && status.contains("30 seconds"))
    );
}

#[test]
fn forced_full_verification_hashes_even_with_a_valid_cache() {
    let fixture = StoreFixture::installed(PACKAGE);
    assert!(fixture.verify().is_verified());
    assert_eq!(fixture.hash_count(), 1);
    assert!(
        fixture
            .store
            .load_and_verify(&none(), true, &mut |_| {})
            .unwrap()
            .is_verified()
    );
    assert_eq!(fixture.hash_count(), 2);
}

#[test]
fn a_touched_package_is_hashed_again() {
    let fixture = StoreFixture::installed(PACKAGE);
    assert!(fixture.verify().is_verified());
    let prepared = fixture.store.prepared_asset_path();
    set_modified(&prepared, modified(&prepared) + Duration::from_secs(300));
    assert!(fixture.verify().is_verified());
    assert_eq!(fixture.hash_count(), 2);
}

#[test]
fn a_silently_corrupted_package_of_the_same_size_still_fails_and_drops_the_cache() {
    let fixture = StoreFixture::installed(PACKAGE);
    assert!(fixture.verify().is_verified());
    let prepared = fixture.store.prepared_asset_path();
    let before = modified(&prepared);
    std::fs::write(&prepared, "x".repeat(PACKAGE.len())).unwrap();
    set_modified(&prepared, before + Duration::from_secs(1));
    let verification = fixture.verify();
    assert!(!verification.is_verified());
    assert!(verification.status.contains("SHA-256"));
    assert!(!fixture.store.verification_cache().cache_path().exists());
}

#[test]
fn corruption_preserving_size_and_write_time_is_caught_only_by_full_verification() {
    let fixture = StoreFixture::installed(PACKAGE);
    assert!(fixture.verify().is_verified());
    let prepared = fixture.store.prepared_asset_path();
    let original = modified(&prepared);
    std::fs::write(&prepared, "x".repeat(PACKAGE.len())).unwrap();
    set_modified(&prepared, original);
    assert!(fixture.verify().is_verified());
    let forced = fixture
        .store
        .load_and_verify(&none(), true, &mut |_| {})
        .unwrap();
    assert!(!forced.is_verified());
    assert!(forced.status.contains("SHA-256"));
    assert!(!fixture.store.verification_cache().cache_path().exists());
}

#[test]
fn a_resized_package_is_rejected_without_hashing_it() {
    let fixture = StoreFixture::installed(PACKAGE);
    assert!(fixture.verify().is_verified());
    std::fs::write(fixture.store.prepared_asset_path(), format!("{PACKAGE}!")).unwrap();
    let verification = fixture.verify();
    assert!(!verification.is_verified());
    assert!(verification.status.contains("size changed"));
    assert_eq!(fixture.hash_count(), 1);
}

#[test]
fn a_cache_whose_digest_disagrees_with_the_record_is_ignored() {
    let fixture = StoreFixture::installed(PACKAGE);
    assert!(fixture.verify().is_verified());
    let cache = fixture
        .store
        .verification_cache()
        .cache_path()
        .to_path_buf();
    let digest = compute_sha256_hex(&fixture.store.prepared_asset_path()).unwrap();
    let text = std::fs::read_to_string(&cache).unwrap();
    std::fs::write(&cache, text.replace(&digest, &"a".repeat(64))).unwrap();
    assert!(fixture.verify().is_verified());
    assert_eq!(fixture.hash_count(), 2);
}

#[test]
fn an_unreadable_cache_degrades_to_hashing_rather_than_failing() {
    for content in ["", "{ not json", "{\"version\":9999}"] {
        let fixture = StoreFixture::installed(PACKAGE);
        assert!(fixture.verify().is_verified());
        std::fs::write(fixture.store.verification_cache().cache_path(), content).unwrap();
        assert!(fixture.verify().is_verified(), "{content:?}");
        assert_eq!(fixture.hash_count(), 2);
    }
}

#[test]
fn backup_recovery_hashes_the_backup_even_when_the_live_cache_is_valid() {
    let fixture = StoreFixture::installed(PACKAGE);
    assert!(fixture.verify().is_verified());
    let prepared = fixture.store.prepared_asset_path();
    let verified_write = modified(&prepared);
    let backup = backup_path(&prepared);
    std::fs::rename(&prepared, &backup).unwrap();
    std::fs::write(&prepared, "z".repeat(PACKAGE.len())).unwrap();
    set_modified(&prepared, verified_write + Duration::from_secs(60));
    let verification = fixture.verify();
    assert!(verification.is_verified());
    assert_eq!(std::fs::read_to_string(&prepared).unwrap(), PACKAGE);
    assert!(!backup.exists());
    // The live package hashed (and failed), then the backup.
    assert_eq!(fixture.hash_count(), 3);
    drop(fixture.root);
}

// --- InstallRootMoverTests -----------------------------------------------------

struct MoveFixture {
    scratch: TempDir,
    default_root: PathBuf,
    paths: ac_host::PathSet,
}

impl MoveFixture {
    fn new() -> Self {
        let scratch = TempDir::new("move");
        let default_root = scratch.join("Local").join("OpenAC");
        let paths = ac_host::PathSet::for_root(&default_root, ac_host::RootSource::Default);
        Self {
            scratch,
            default_root,
            paths,
        }
    }

    fn mover(&self, same_volume: Option<bool>) -> InstallRootMover {
        InstallRootMover::new(
            &self.paths,
            &self.default_root,
            same_volume.map(|same| Box::new(move |_: &Path, _: &Path| same) as SameVolume),
        )
    }

    fn write(&self, relative: &str, content: &str) {
        let path = relative
            .split('/')
            .fold(self.default_root.clone(), |path, part| path.join(part));
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(path, content).unwrap();
    }

    fn seed_install(&self) {
        self.write("app/current.json", "{}");
        self.write("app/0.1.15/openac.exe", "x");
        self.write("game/pak/acdream.pak", "pak");
        self.write(
            "game/install.json",
            &record_naming(
                &self
                    .default_root
                    .join("game")
                    .join("pak")
                    .join("acdream.pak"),
            ),
        );
        self.write("settings/settings.json", "{}");
        self.write("plugins/p/plugin.json", "{}");
        self.write("plugins/p/data/a.json", "{}");
        self.write("cache/plugins.json", "{}");
    }

    fn pointer(&self) -> PathBuf {
        root_pointer::path_for(&self.default_root)
    }
}

fn record_naming(path: &Path) -> String {
    let mut text = String::from("{ \"preparedAssetPath\": ");
    ac_host::json::write_string(&mut text, &path.to_string_lossy());
    text.push_str(" }");
    text
}

fn recorded_asset(record: &Path) -> String {
    let document = ac_host::json::parse(&std::fs::read(record).unwrap()).unwrap();
    let members = ac_host::json::object_members(&document).unwrap();
    let member = members
        .iter()
        .find(|member| member.name == "preparedAssetPath")
        .unwrap();
    ac_host::json::string_or_null(&member.value)
        .unwrap()
        .unwrap()
}

// OpenAC InstallRootMoverTests.MoveCarriesTheInstallAndPointsTheDefaultAtIt.
#[test]
fn a_move_carries_the_install_and_points_the_default_at_it() {
    for same_volume in [true, false] {
        let fixture = MoveFixture::new();
        fixture.seed_install();
        let target = fixture.scratch.join("Games").join("OpenAC");

        let result = fixture
            .mover(Some(same_volume))
            .move_to(&target, &mut |_| {});

        assert!(result.moved, "{}", result.message);
        assert!(result.leftovers.is_empty(), "{:?}", result.leftovers);
        assert!(target.join("app").join("current.json").is_file());
        assert!(
            target
                .join("game")
                .join("pak")
                .join("acdream.pak")
                .is_file()
        );
        assert!(target.join("settings").join("settings.json").is_file());
        let kept = target.join("plugins").join("p").join("data").join("a.json");
        assert!(kept.is_file());
        assert!(!target.join("cache").exists());
        assert_eq!(
            recorded_asset(&target.join("game").join("install.json")),
            target
                .join("game")
                .join("pak")
                .join("acdream.pak")
                .to_string_lossy()
        );
        let pointer = std::fs::read_to_string(fixture.pointer()).unwrap();
        assert_eq!(root_pointer::parse(&pointer), Some(target));
        let left: Vec<PathBuf> = std::fs::read_dir(&fixture.default_root)
            .unwrap()
            .map(|entry| entry.unwrap().path())
            .collect();
        assert_eq!(left, [fixture.pointer()]);
    }
}

// OpenAC InstallRootMoverTests.MoveIsRefusedWhileASessionRuns.
#[test]
fn a_move_is_refused_while_a_session_runs() {
    let fixture = MoveFixture::new();
    fixture.seed_install();
    let barrier = UpdateSessionBarrier::new(&fixture.paths.data_directory);
    let _session = barrier.acquire_session().unwrap();

    let result = fixture
        .mover(Some(true))
        .move_to(&fixture.scratch.join("Games").join("OpenAC"), &mut |_| {});

    assert!(!result.moved);
    assert_eq!(result.message, MOVE_SESSION_REFUSAL);
    assert!(
        fixture
            .default_root
            .join("settings")
            .join("settings.json")
            .is_file()
    );
    assert!(!fixture.pointer().exists());
}

// OpenAC InstallRootMoverTests.MoveIsRefusedWhileAHandStartedClientRuns.
#[test]
fn a_move_is_refused_while_a_hand_started_client_runs() {
    let fixture = MoveFixture::new();
    fixture.seed_install();
    let client = ac_host::InstallSessionLease::try_acquire_shared(&fixture.paths).unwrap();

    let result = fixture
        .mover(Some(true))
        .move_to(&fixture.scratch.join("Games").join("OpenAC"), &mut |_| {});

    assert!(client.is_some());
    assert!(!result.moved);
    assert_eq!(result.message, MOVE_SESSION_REFUSAL);
    assert_eq!(
        ac_host::InstallSessionLease::lock_path(&fixture.paths),
        UpdateSessionBarrier::new(&fixture.paths.data_directory).lock_path()
    );
}

// OpenAC InstallRootMoverTests.AnExplicitlyNamedRootCannotBeMovedFromTheLauncher.
#[test]
fn an_explicitly_named_root_cannot_be_moved_from_the_launcher() {
    let fixture = MoveFixture::new();
    let explicit = ac_host::PathSet::for_root(
        &fixture.scratch.join("explicit"),
        ac_host::RootSource::Explicit,
    );
    let mover = InstallRootMover::new(&explicit, &fixture.default_root, None);

    assert!(mover.unavailable_reason().is_some());
    assert!(
        !mover
            .move_to(&fixture.scratch.join("Games"), &mut |_| {})
            .moved
    );
}

// OpenAC InstallRootMoverTests.TargetsThatWouldMixOrNestAreRefused.
#[test]
fn targets_that_would_mix_or_nest_are_refused() {
    let fixture = MoveFixture::new();
    fixture.seed_install();
    let occupied = fixture.scratch.join("occupied");
    std::fs::create_dir_all(&occupied).unwrap();
    std::fs::write(occupied.join("x.txt"), "x").unwrap();
    let mover = fixture.mover(None);

    assert!(mover.validate_target(&occupied).is_some());
    assert!(
        mover
            .validate_target(&fixture.default_root.join("inner"))
            .is_some()
    );
    assert!(mover.validate_target(&fixture.scratch.path).is_some());
    assert!(mover.validate_target(Path::new("relative")).is_some());
    assert!(
        mover
            .validate_target(&fixture.scratch.join("fresh"))
            .is_none()
    );
}

// OpenAC InstallRootMoverTests.MovingBackToTheDefaultRemovesThePointer.
#[test]
fn moving_back_to_the_default_removes_the_pointer() {
    let fixture = MoveFixture::new();
    let elsewhere = fixture.scratch.join("Games").join("OpenAC");
    root_pointer::write(&fixture.default_root, &elsewhere).unwrap();
    let pointed = ac_host::PathSet::for_root(&elsewhere, ac_host::RootSource::Pointer);
    std::fs::create_dir_all(&pointed.config_directory).unwrap();
    std::fs::write(pointed.settings_file(), "{}").unwrap();
    let mover = InstallRootMover::new(
        &pointed,
        &fixture.default_root,
        Some(Box::new(|_: &Path, _: &Path| true)),
    );

    let result = mover.move_to(&fixture.default_root, &mut |_| {});

    assert!(result.moved, "{}", result.message);
    assert!(
        fixture
            .default_root
            .join("settings")
            .join("settings.json")
            .is_file()
    );
    assert!(!fixture.pointer().exists());
    assert!(!elsewhere.exists());
}

/// A file held open with no sharing, as another program holding it would.
#[cfg(windows)]
fn hold_exclusively(path: &Path) -> std::fs::File {
    use std::os::windows::fs::OpenOptionsExt;
    std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .share_mode(0)
        .open(path)
        .unwrap()
}

// OpenAC InstallRootMoverTests.AFailedCopyLeavesTheOldInstallUntouched (Windows lane).
#[cfg(windows)]
#[test]
fn a_failed_copy_leaves_the_old_install_untouched() {
    let fixture = MoveFixture::new();
    fixture.seed_install();
    let target = fixture.scratch.join("Games").join("OpenAC");
    let result = {
        let _held = hold_exclusively(&fixture.default_root.join("settings").join("settings.json"));
        fixture.mover(Some(false)).move_to(&target, &mut |_| {})
    };

    assert!(!result.moved);
    assert!(!fixture.pointer().exists());
    assert!(
        fixture
            .default_root
            .join("app")
            .join("current.json")
            .is_file()
    );
    assert!(!target.exists());
}

// OpenAC InstallRootMoverTests.ARefusedRenameIsRolledBack (Windows lane).
#[cfg(windows)]
#[test]
fn a_refused_rename_is_rolled_back() {
    let fixture = MoveFixture::new();
    fixture.seed_install();
    let target = fixture.scratch.join("Games").join("OpenAC");
    let mover = fixture.mover(Some(true));
    let result = {
        let _held = hold_exclusively(&fixture.default_root.join("settings").join("settings.json"));
        mover.move_to(&target, &mut |_| {})
    };

    assert!(!result.moved);
    assert!(
        fixture
            .default_root
            .join("app")
            .join("current.json")
            .is_file()
    );
    let package = fixture
        .default_root
        .join("game")
        .join("pak")
        .join("acdream.pak");
    assert!(package.is_file());
    assert!(!fixture.pointer().exists());
    // Nothing half-made is left at the target, so the same folder can be
    // tried again once the file is closed.
    assert!(!target.exists());
    let retry = mover.move_to(&target, &mut |_| {});
    assert!(retry.moved, "{}", retry.message);
}

// OpenAC InstallRootMoverTests.AnUnreadableInstallRecordDoesNotStopTheMove.
#[test]
fn an_unreadable_install_record_does_not_stop_the_move() {
    let fixture = MoveFixture::new();
    fixture.seed_install();
    fixture.write("game/install.json", "not json");
    let target = fixture.scratch.join("Games").join("OpenAC");

    let result = fixture.mover(Some(true)).move_to(&target, &mut |_| {});

    assert!(result.moved, "{}", result.message);
    let pointer = std::fs::read_to_string(fixture.pointer()).unwrap();
    assert_eq!(root_pointer::parse(&pointer), Some(target));
    assert!(
        result
            .leftovers
            .iter()
            .any(|line| line.contains("next start"))
    );
}

// OpenAC InstallRootMoverTests.RepairPointsMovedRecordsAtThisRootsContentOnly.
#[test]
fn repair_points_moved_records_at_this_roots_content_only() {
    let fixture = MoveFixture::new();
    let data = fixture.paths.game_data_directory();
    let package = data.join("pak").join("acdream.pak");
    let record = data.join("install.json");
    let elsewhere = fixture
        .scratch
        .join("Elsewhere")
        .join("OpenAC")
        .join("game")
        .join("pak")
        .join("acdream.pak");
    fixture.write("game/install.json", &record_naming(&elsewhere));

    assert!(repair_content_records(&fixture.paths).unwrap().is_empty());

    fixture.write("game/pak/acdream.pak", "pak");
    assert_eq!(
        repair_content_records(&fixture.paths).unwrap(),
        std::slice::from_ref(&record)
    );
    assert_eq!(recorded_asset(&record), package.to_string_lossy());
    assert!(repair_content_records(&fixture.paths).unwrap().is_empty());

    fixture.write(
        "game/install.json",
        &record_naming(&data.join("custom.pak")),
    );
    assert!(repair_content_records(&fixture.paths).unwrap().is_empty());
}
