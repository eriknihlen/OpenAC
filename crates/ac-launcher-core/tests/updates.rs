//! Release manifest, downloads, safe zip extraction, the session barrier,
//! the versioned client store and the updater. Ported from OpenAC
//! `tests/AcDream.Launcher.Core.Tests/Updates/` (ReleaseTransportTests,
//! SafeZipExtractorTests, UpdateSessionBarrierTests, ClientVersionStoreTests,
//! LauncherUpdaterIntegrationTests). The loopback HTTP fixture is an
//! in-memory transport here: the launcher talks to the network only through
//! `HttpTransport`.

mod common;

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::integrity::sha256_hex;
use ac_launcher_core::updates::{
    ArtifactDownloadProgress, ClientVersionResolution, ClientVersionState, ClientVersionStore,
    ExtractedFileRecord, LAUNCHER_FINGERPRINT_FILE_NAME, LauncherUpdatePhase, LauncherUpdater,
    LauncherVersion, PREVIOUS_MANIFEST_URI, PRODUCTION_MANIFEST_URI, ReleaseArtifact,
    ReleaseManifest, ReleaseManifestClient, ReleaseManifestSource, SafeZipExtractionLimits,
    SafeZipExtractor, UpdateError, UpdateSessionBarrier, VerifiedArtifactDownloader, detect_rid,
    executables_for_payload, launcher_fingerprint_file_name, validate_required_executables,
};
use ac_launcher_core::url::WebUrl;
use common::TempDir;
use common::fixtures::{
    self, Compression, FakeServer, Route, client_zip, create_zip, launcher_zip,
};

fn none() -> CancelToken {
    CancelToken::none()
}

fn rid() -> String {
    detect_rid().expect("a supported test platform")
}

fn program(rid: &str) -> String {
    format!(
        "openac{}",
        if rid.starts_with("win-") { ".exe" } else { "" }
    )
}

// --- ReleaseManifestClientTests ---------------------------------------------

#[test]
fn the_default_feed_is_production_and_another_feed_is_a_local_override() {
    let server = FakeServer::new();
    let production = ReleaseManifestClient::production_manifest_uri();
    assert_eq!(production.to_string(), PRODUCTION_MANIFEST_URI);

    // The production feed is read as production: a redirect to loopback HTTP
    // is refused before any plaintext request.
    server.add_route(
        PRODUCTION_MANIFEST_URI,
        Route::Redirect {
            status: 302,
            location: "http://127.0.0.1/manifest.json".into(),
        },
    );
    let client = ReleaseManifestClient::for_feed(server.transport(), &production).unwrap();
    assert_eq!(client.manifest_uri(), &production);
    let error = client.fetch(&none()).unwrap_err().to_string();
    assert!(error.contains("HTTPS"), "{error}");
    assert_eq!(server.requests(), [PRODUCTION_MANIFEST_URI]);

    // Any other feed goes through the override's rules: loopback HTTP is
    // allowed, other plain HTTP is not.
    let loopback = WebUrl::parse(&FakeServer::url("manifest.json")).unwrap();
    let client = ReleaseManifestClient::for_feed(server.transport(), &loopback).unwrap();
    assert_eq!(client.manifest_uri(), &loopback);
    let plain = WebUrl::parse("http://updates.example.test/manifest.json").unwrap();
    assert!(ReleaseManifestClient::for_feed(server.transport(), &plain).is_err());
}

#[test]
fn local_update_feed_override_accepts_only_secure_or_loopback_feeds() {
    let server = FakeServer::new();
    for value in [
        "https://updates.example.test/manifest.json",
        "http://127.0.0.1:43119/manifest.json",
        "http://localhost:43119/manifest.json",
    ] {
        ReleaseManifestClient::local_update_feed_override(server.transport(), value)
            .unwrap_or_else(|error| panic!("{value}: {error}"));
    }
}

#[test]
fn local_update_feed_override_rejects_remote_http_and_credential_like_uris() {
    let server = FakeServer::new();
    for value in [
        "http://updates.example.test/manifest.json",
        "file:///tmp/manifest.json",
        "https://user:secret@updates.example.test/manifest.json",
        "https://updates.example.test/manifest.json?token=secret",
        "https://updates.example.test/manifest.json#fragment",
    ] {
        assert!(
            ReleaseManifestClient::local_update_feed_override(server.transport(), value).is_err(),
            "{value}"
        );
    }
}

fn configure_release(
    server: &FakeServer,
    version: &str,
    minimum: &str,
    rid: &str,
    client: &[u8],
    launcher: &[u8],
) {
    let client_url = FakeServer::url("client.zip");
    let launcher_url = FakeServer::url("launcher.zip");
    server.add(&client_url, client);
    server.add(&launcher_url, launcher);
    server.add(
        &FakeServer::url("manifest.json"),
        &fixtures::manifest(
            version,
            minimum,
            rid,
            &client_url,
            client,
            &launcher_url,
            launcher,
        ),
    );
}

fn loopback_client(server: &FakeServer) -> ReleaseManifestClient {
    ReleaseManifestClient::local_update_feed_override(
        server.transport(),
        &FakeServer::url("manifest.json"),
    )
    .unwrap()
}

#[test]
fn fetches_strict_manifest_from_loopback_and_pins_production_feed() {
    let server = FakeServer::new();
    let client = client_zip("win-x64", "client");
    let launcher = launcher_zip("win-x64", "launcher");
    configure_release(&server, "2.1.0", "1.5.0", "win-x64", &client, &launcher);

    let manifest = loopback_client(&server).fetch(&none()).unwrap();

    assert_eq!(manifest.version.value(), "2.1.0");
    assert_eq!(
        manifest.require_client("win-x64").unwrap().size,
        client.len() as u64
    );
    assert_eq!(
        PRODUCTION_MANIFEST_URI,
        "https://github.com/eriknihlen/OpenAC/releases/latest/download/openac-manifest.json"
    );
    assert!(WebUrl::parse(PRODUCTION_MANIFEST_URI).unwrap().is_https());
}

// --- The feed's move: the new repository first, the old one while it has none

const NEW_FEED: &str = "https://example.test/new/latest/openac-manifest.json";
const OLD_FEED: &str = "https://example.test/old/latest/manifest.json";

fn moving_client(server: &FakeServer) -> ReleaseManifestClient {
    ReleaseManifestClient::for_transport_test(
        server.transport(),
        WebUrl::parse(NEW_FEED).unwrap(),
        false,
    )
    .unwrap()
    .with_fallback(WebUrl::parse(OLD_FEED).unwrap())
}

fn fingerprint_json(version: &str) -> String {
    format!(
        r#"{{"schemaVersion":1,"version":"{version}","fingerprint":"{}"}}"#,
        "c".repeat(64)
    )
}

#[test]
fn production_reads_the_new_repository_and_falls_back_to_the_old_one() {
    let client = ReleaseManifestClient::production(FakeServer::new().transport());
    assert_eq!(client.manifest_uri().to_string(), PRODUCTION_MANIFEST_URI);
    assert_eq!(
        client.fallback_uri().map(ToString::to_string).as_deref(),
        Some(PREVIOUS_MANIFEST_URI)
    );
    assert_eq!(
        PREVIOUS_MANIFEST_URI,
        "https://github.com/eriknihlen/openac-rust/releases/latest/download/manifest.json"
    );
    // An override names one feed and has no fallback.
    assert!(loopback_client(&FakeServer::new()).fallback_uri().is_none());
}

#[test]
fn the_fingerprint_is_named_after_its_manifest() {
    let name = |uri: &str| launcher_fingerprint_file_name(&WebUrl::parse(uri).unwrap());
    assert_eq!(
        name(PRODUCTION_MANIFEST_URI),
        "openac-launcher-fingerprint.json"
    );
    assert_eq!(name(PREVIOUS_MANIFEST_URI), LAUNCHER_FINGERPRINT_FILE_NAME);
    assert_eq!(
        name("https://example.test/feed.json"),
        LAUNCHER_FINGERPRINT_FILE_NAME
    );
}

#[test]
fn a_feed_without_the_file_falls_back_and_reads_the_fingerprint_there() {
    let server = FakeServer::new();
    server.add(OLD_FEED, valid_json().as_bytes());
    server.add(
        "https://example.test/old/latest/launcher-fingerprint.json",
        fingerprint_json("2.0.0").as_bytes(),
    );
    let client = moving_client(&server);

    let manifest = client.fetch(&none()).unwrap();
    assert_eq!(manifest.version.value(), "2.0.0");
    assert_eq!(client.answered_uri().to_string(), OLD_FEED);
    let fingerprint = client.fetch_launcher_fingerprint(&none()).unwrap();
    assert_eq!(fingerprint.unwrap().fingerprint, "c".repeat(64));
    assert_eq!(
        server.requests(),
        [
            NEW_FEED,
            OLD_FEED,
            "https://example.test/old/latest/launcher-fingerprint.json"
        ]
    );
}

#[test]
fn a_feed_with_the_file_is_read_alone_with_its_own_fingerprint_name() {
    let server = FakeServer::new();
    server.add(NEW_FEED, valid_json().as_bytes());
    server.add(OLD_FEED, valid_json().replace("2.0.0", "9.0.0").as_bytes());
    server.add(
        "https://example.test/new/latest/openac-launcher-fingerprint.json",
        fingerprint_json("2.0.0").as_bytes(),
    );
    let client = moving_client(&server);

    assert_eq!(client.fetch(&none()).unwrap().version.value(), "2.0.0");
    assert!(
        client
            .fetch_launcher_fingerprint(&none())
            .unwrap()
            .is_some()
    );
    assert_eq!(
        server.requests(),
        [
            NEW_FEED,
            "https://example.test/new/latest/openac-launcher-fingerprint.json"
        ]
    );
}

#[test]
fn a_failing_or_broken_feed_is_an_error_and_never_falls_back() {
    for broken in [false, true] {
        let server = FakeServer::new();
        if broken {
            server.add(NEW_FEED, b"{}");
        } else {
            server.add_route(NEW_FEED, Route::Status(500));
        }
        server.add(OLD_FEED, valid_json().as_bytes());
        let client = moving_client(&server);
        assert!(client.fetch(&none()).is_err());
        assert_eq!(server.requests(), [NEW_FEED]);
    }
}

#[test]
fn a_feed_missing_everywhere_says_so() {
    let server = FakeServer::new();
    let error = moving_client(&server)
        .fetch(&none())
        .unwrap_err()
        .to_string();
    assert!(error.contains("404"), "{error}");
    assert_eq!(server.requests(), [NEW_FEED, OLD_FEED]);
}

fn valid_json() -> String {
    format!(
        r#"{{"schemaVersion":1,"version":"2.0.0","minimumLauncherVersion":"1.0.0","clients":{{"win-x64":{{"url":"https://example.test/client","sha256":"{}","size":12}}}},"launchers":{{"win-x64":{{"url":"https://example.test/launcher","sha256":"{}","size":12}}}}}}"#,
        "a".repeat(64),
        "b".repeat(64)
    )
}

#[test]
fn rejects_wrong_version_rid_hash_size_minimum_and_unknown_or_duplicate_fields() {
    let valid = valid_json();
    assert!(ReleaseManifest::parse(valid.as_bytes(), false).is_ok());
    let cases = [
        "{}".to_owned(),
        valid.replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
        valid.replace("\"version\":\"2.0.0\"", "\"version\":\"02.0.0\""),
        valid.replace(
            "\"minimumLauncherVersion\":\"1.0.0\"",
            "\"minimumLauncherVersion\":\"3.0.0\"",
        ),
        valid.replace("win-x64", "WIN_X64"),
        valid.replace(&"a".repeat(64), "1234"),
        valid.replacen("\"size\":12", "\"size\":0", 1),
        valid.replacen("\"size\":12", "\"size\":12,\"extra\":true", 1),
        valid.replace(
            "\"version\":\"2.0.0\"",
            "\"version\":\"2.0.0\",\"version\":\"2.0.1\"",
        ),
        valid.replace("https://example.test/client", "http://example.test/client"),
    ];
    for json in cases {
        assert!(
            matches!(
                ReleaseManifest::parse(json.as_bytes(), false),
                Err(UpdateError::Failed { .. })
            ),
            "{json}"
        );
    }
}

#[test]
fn production_manifest_rejects_loopback_http_artifacts() {
    for (section, artifact) in [("clients", "client"), ("launchers", "launcher")] {
        let json = valid_json().replace(
            &format!("https://example.test/{artifact}"),
            &format!("http://127.0.0.1/{artifact}"),
        );
        let error = ReleaseManifest::parse(json.as_bytes(), false)
            .unwrap_err()
            .to_string();
        assert!(error.contains(section), "{error}");
        assert!(error.contains("HTTPS"), "{error}");
    }
}

#[test]
fn production_redirect_to_loopback_is_rejected_before_plaintext_request() {
    let server = FakeServer::new();
    server.add_route(
        PRODUCTION_MANIFEST_URI,
        Route::Redirect {
            status: 302,
            location: "http://127.0.0.1/manifest.json".into(),
        },
    );
    let client = ReleaseManifestClient::production(server.transport());
    let error = client.fetch(&none()).unwrap_err().to_string();
    assert!(error.contains("HTTPS"), "{error}");
    assert_eq!(server.requests(), [PRODUCTION_MANIFEST_URI]);
}

#[test]
fn https_redirect_downgrade_is_rejected_before_intermediate_hop() {
    let server = FakeServer::new();
    let start = "https://example.test/start";
    server.add_route(
        start,
        Route::Redirect {
            status: 307,
            location: "http://example.test/plaintext-hop".into(),
        },
    );
    let client = ReleaseManifestClient::for_transport_test(
        server.transport(),
        WebUrl::parse(start).unwrap(),
        false,
    )
    .unwrap();
    assert!(client.fetch(&none()).is_err());
    assert_eq!(server.requests(), [start]);
}

#[test]
fn redirect_loop_is_rejected_without_repeating_a_request() {
    let server = FakeServer::new();
    let first = "https://example.test/first";
    let second = "https://example.test/second";
    server.add_route(
        first,
        Route::Redirect {
            status: 308,
            location: second.into(),
        },
    );
    server.add_route(
        second,
        Route::Redirect {
            status: 308,
            location: first.into(),
        },
    );
    let client = ReleaseManifestClient::for_transport_test(
        server.transport(),
        WebUrl::parse(first).unwrap(),
        false,
    )
    .unwrap();
    let error = client.fetch(&none()).unwrap_err().to_string();
    assert!(error.to_lowercase().contains("loop"), "{error}");
    assert_eq!(server.requests(), [first, second]);
}

#[test]
fn redirect_limit_rejects_before_requesting_the_sixth_hop() {
    let server = FakeServer::new();
    server.fallback(|_, index| Route::Redirect {
        status: 302,
        location: format!("https://example.test/hop-{}", index + 1),
    });
    let client = ReleaseManifestClient::for_transport_test(
        server.transport(),
        WebUrl::parse("https://example.test/hop-0").unwrap(),
        false,
    )
    .unwrap();
    let error = client.fetch(&none()).unwrap_err().to_string();
    assert!(error.contains("5 redirects"), "{error}");
    let requests = server.requests();
    assert_eq!(requests.len(), 6);
    assert_eq!(requests[5], "https://example.test/hop-5");
}

// --- VerifiedArtifactDownloaderTests ----------------------------------------

fn artifact(url: &str, sha256: &str, size: u64) -> ReleaseArtifact {
    ReleaseArtifact {
        url: WebUrl::parse(url).unwrap(),
        sha256: sha256.to_owned(),
        size,
    }
}

#[test]
fn streams_to_staging_with_progress_and_exact_digest() {
    let root = TempDir::new("download");
    let server = FakeServer::new();
    let bytes: Vec<u8> = (0..200_000u32).map(|value| value as u8).collect();
    let url = FakeServer::url("artifact");
    server.add_slow(&url, &bytes, 4096, Duration::ZERO);
    let downloader = VerifiedArtifactDownloader::new(server.transport());
    let mut progress = Vec::new();
    let destination = root.join("artifact.zip");

    let result = downloader
        .download_artifact(
            &artifact(&url, &sha256_hex(&bytes), bytes.len() as u64),
            &destination,
            &mut |value: ArtifactDownloadProgress| progress.push(value),
            &none(),
        )
        .unwrap();

    assert_eq!(result.size, bytes.len() as u64);
    assert_eq!(result.sha256, sha256_hex(&bytes));
    assert_eq!(std::fs::read(&destination).unwrap(), bytes);
    assert_eq!(progress[0].bytes_received, 0);
    assert_eq!(progress.last().unwrap().bytes_received, bytes.len() as u64);
}

#[test]
fn wrong_size_header_partial_body_and_hash_delete_staging() {
    for failure in ["short", "header", "hash"] {
        let root = TempDir::new("download-failure");
        let server = FakeServer::new();
        let bytes = b"verified bytes".to_vec();
        let expected = if failure == "short" {
            bytes.len() as u64 + 5
        } else {
            bytes.len() as u64
        };
        let declared = if failure == "header" {
            bytes.len() as u64 + 1
        } else {
            expected
        };
        let url = FakeServer::url("artifact");
        server.add_declared(&url, &bytes, Some(declared));
        let downloader = VerifiedArtifactDownloader::new(server.transport());
        let destination = root.join(format!("{failure}.zip"));
        let hash = if failure == "hash" {
            "0".repeat(64)
        } else {
            sha256_hex(&bytes)
        };
        let result = downloader.download_artifact(
            &artifact(&url, &hash, expected),
            &destination,
            &mut |_| {},
            &none(),
        );
        assert!(
            matches!(result, Err(UpdateError::Failed { .. })),
            "{failure}"
        );
        assert!(!destination.exists(), "{failure}");
    }
}

#[test]
fn cancellation_deletes_partial_staging() {
    let root = TempDir::new("download-cancel");
    let server = FakeServer::new();
    let bytes = vec![7u8; 2 * 1024 * 1024];
    let url = FakeServer::url("slow");
    server.add_slow(&url, &bytes, 1024, Duration::from_millis(3));
    let downloader = VerifiedArtifactDownloader::new(server.transport());
    let destination = root.join("cancel.zip");
    let cancel = CancelToken::new();
    let canceller = cancel.clone();
    let timer = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(40));
        canceller.cancel();
    });
    let result = downloader.download_artifact(
        &artifact(&url, &sha256_hex(&bytes), bytes.len() as u64),
        &destination,
        &mut |_| {},
        &cancel,
    );
    timer.join().unwrap();
    assert!(result.unwrap_err().is_cancelled());
    assert!(!destination.exists());
}

#[test]
fn refuses_and_preserves_pre_existing_caller_file() {
    let root = TempDir::new("download-preserve");
    let server = FakeServer::new();
    let bytes = b"network".to_vec();
    let url = FakeServer::url("artifact");
    server.add(&url, &bytes);
    let downloader = VerifiedArtifactDownloader::new(server.transport());
    let destination = root.join("already-owned.zip");
    std::fs::write(&destination, "preserve").unwrap();
    assert!(
        downloader
            .download_artifact(
                &artifact(&url, &sha256_hex(&bytes), bytes.len() as u64),
                &destination,
                &mut |_| {},
                &none(),
            )
            .is_err()
    );
    assert_eq!(std::fs::read_to_string(&destination).unwrap(), "preserve");
}

#[test]
fn https_artifact_redirect_downgrade_is_rejected_before_plaintext_hop() {
    let root = TempDir::new("download-redirect");
    let server = FakeServer::new();
    let url = "https://example.test/artifact";
    server.add_route(
        url,
        Route::Redirect {
            status: 302,
            location: "http://example.test/plaintext".into(),
        },
    );
    let downloader = VerifiedArtifactDownloader::new(server.transport());
    let destination = root.join("redirect.zip");
    let error = downloader
        .download_artifact(
            &artifact(url, &"a".repeat(64), 12),
            &destination,
            &mut |_| {},
            &none(),
        )
        .unwrap_err()
        .to_string();
    assert!(error.contains("HTTPS"), "{error}");
    assert_eq!(server.requests(), [url]);
    assert!(!destination.exists());
}

#[test]
fn maximum_bytes_streams_and_verifies_hash_when_size_is_unknown_up_front() {
    let root = TempDir::new("download-capped");
    let server = FakeServer::new();
    let bytes = b"plugin release bytes".to_vec();
    let url = FakeServer::url("plugin.zip");
    server.add(&url, &bytes);
    let downloader = VerifiedArtifactDownloader::new(server.transport());
    let destination = root.join("max-bytes.zip");
    let result = downloader
        .download_capped(
            &WebUrl::parse(&url).unwrap(),
            &sha256_hex(&bytes),
            1024,
            &destination,
            &mut |_| {},
            &none(),
        )
        .unwrap();
    assert_eq!(result.size, bytes.len() as u64);
    assert_eq!(result.sha256, sha256_hex(&bytes));
    assert_eq!(std::fs::read(&destination).unwrap(), bytes);
}

#[test]
fn maximum_bytes_rejects_an_over_cap_size_header_before_streaming() {
    let root = TempDir::new("download-capped-header");
    let server = FakeServer::new();
    let bytes = b"plugin release bytes".to_vec();
    let url = FakeServer::url("plugin.zip");
    server.add_declared(&url, &bytes, Some(5000));
    let downloader = VerifiedArtifactDownloader::new(server.transport());
    let destination = root.join("max-bytes-header.zip");
    assert!(
        downloader
            .download_capped(
                &WebUrl::parse(&url).unwrap(),
                &sha256_hex(&bytes),
                1024,
                &destination,
                &mut |_| {},
                &none(),
            )
            .is_err()
    );
    assert!(!destination.exists());
}

#[test]
fn maximum_bytes_rejects_an_over_cap_stream_and_deletes_staging() {
    let root = TempDir::new("download-capped-stream");
    let server = FakeServer::new();
    let bytes = vec![3u8; 2048];
    let url = FakeServer::url("plugin.zip");
    server.add_declared(&url, &bytes, Some(100));
    let downloader = VerifiedArtifactDownloader::new(server.transport());
    let destination = root.join("max-bytes-stream.zip");
    assert!(
        downloader
            .download_capped(
                &WebUrl::parse(&url).unwrap(),
                &sha256_hex(&bytes),
                1024,
                &destination,
                &mut |_| {},
                &none(),
            )
            .is_err()
    );
    assert!(!destination.exists());
}

// --- SafeZipExtractorTests ---------------------------------------------------

const EXECUTABLE: u32 = 0o755;
const READABLE: u32 = 0o644;

fn write_archive(root: &TempDir, name: &str, content: &[u8]) -> PathBuf {
    let path = root.join(name);
    std::fs::write(&path, content).unwrap();
    path
}

fn mode_of(files: &[ExtractedFileRecord], path: &str) -> u32 {
    files
        .iter()
        .find(|file| file.path == path)
        .unwrap()
        .unix_mode
}

type Applied = Arc<Mutex<Vec<(String, u32)>>>;

fn recording_extractor(ignore_declared: bool) -> (SafeZipExtractor, Applied) {
    let applied: Applied = Arc::default();
    let record = Arc::clone(&applied);
    let extractor = SafeZipExtractor::new(SafeZipExtractionLimits::default(), ignore_declared)
        .with_mode_setter(move |path: &Path, mode| {
            record.lock().unwrap().push((
                path.file_name().unwrap().to_string_lossy().into_owned(),
                mode,
            ));
            Ok(())
        });
    (extractor, applied)
}

fn applied_mode(applied: &Applied, name: &str) -> u32 {
    applied
        .lock()
        .unwrap()
        .iter()
        .find(|(file, _)| file == name)
        .map(|(_, mode)| *mode)
        .unwrap()
}

#[test]
fn extracts_portable_tree_with_hashes_and_executable_mode() {
    let root = TempDir::new("zip-valid");
    let archive = create_zip(
        &[
            ("bin/", b"", Some(0x41ED)),
            ("bin/client", b"client", Some(0x81ED)),
            ("data/value.txt", b"value", Some(0x81A4)),
        ],
        Compression::Stored,
    );
    let zip = write_archive(&root, "valid.zip", &archive);
    let destination = root.join("valid");
    let files = SafeZipExtractor::default()
        .extract(&zip, &destination, &[], &none())
        .unwrap();
    let paths: Vec<&str> = files.iter().map(|file| file.path.as_str()).collect();
    assert_eq!(paths, ["bin/client", "data/value.txt"]);
    assert_eq!(files[0].unix_mode, 0o755);
    assert_eq!(files[0].sha256, sha256_hex(b"client"));
    assert_eq!(
        std::fs::read_to_string(destination.join("data").join("value.txt")).unwrap(),
        "value"
    );
}

#[test]
fn deflated_entries_extract() {
    let root = TempDir::new("zip-deflate");
    let content: String = (0..5000)
        .map(|n: u32| format!("{} ", n.wrapping_mul(2_654_435_761)))
        .collect();
    let archive = create_zip(
        &[("text.txt", content.as_bytes(), Some(0x81A4))],
        Compression::Smallest,
    );
    let zip = write_archive(&root, "deflate.zip", &archive);
    let destination = root.join("deflate");
    let files = SafeZipExtractor::default()
        .extract(&zip, &destination, &[], &none())
        .unwrap();
    assert_eq!(files[0].sha256, sha256_hex(content.as_bytes()));
    assert_eq!(
        std::fs::read_to_string(destination.join("text.txt")).unwrap(),
        content
    );
}

#[test]
fn windows_built_payload_still_marks_its_executables_on_a_unix_host() {
    let root = TempDir::new("zip-windows-built");
    let archive = create_zip(
        &[
            ("openac", b"gui", None),
            ("assets/readme.txt", b"readme", None),
        ],
        Compression::Stored,
    );
    let zip = write_archive(&root, "windows-built.zip", &archive);
    let (extractor, applied) = recording_extractor(false);
    let files = extractor
        .extract(
            &zip,
            &root.join("windows-built"),
            &executables_for_payload("linux-x64", false),
            &none(),
        )
        .unwrap();
    assert_eq!(applied_mode(&applied, "openac"), EXECUTABLE);
    assert_eq!(applied_mode(&applied, "readme.txt"), READABLE);
    assert_eq!(mode_of(&files, "openac"), 0o755);
    assert_eq!(mode_of(&files, "assets/readme.txt"), 0o644);
    validate_required_executables(&files, "linux-x64", false).unwrap();
}

#[test]
fn mac_payload_requires_owner_execute_permission_for_every_required_host() {
    let executable = [ExtractedFileRecord::new("openac", "a".repeat(64), 1, 0o755)];
    validate_required_executables(&executable, "osx-arm64", false).unwrap();
    let not_executable = [ExtractedFileRecord::new("openac", "a".repeat(64), 1, 0o644)];
    let error = validate_required_executables(&not_executable, "osx-arm64", false)
        .unwrap_err()
        .to_string();
    assert!(
        error.contains("Unix release executable 'openac'"),
        "{error}"
    );
}

#[cfg(unix)]
#[test]
fn unix_writes_the_marked_modes_to_the_real_file_system() {
    use std::os::unix::fs::PermissionsExt;
    let root = TempDir::new("zip-linux-modes");
    let archive = create_zip(
        &[
            ("openac", b"gui", None),
            ("assets/readme.txt", b"readme", None),
        ],
        Compression::Stored,
    );
    let zip = write_archive(&root, "linux-modes.zip", &archive);
    let destination = root.join("linux-modes");
    SafeZipExtractor::default()
        .extract(
            &zip,
            &destination,
            &executables_for_payload("linux-x64", false),
            &none(),
        )
        .unwrap();
    let mode = |path: PathBuf| std::fs::metadata(path).unwrap().permissions().mode() & 0o777;
    assert_eq!(mode(destination.join("openac")), EXECUTABLE);
    assert_eq!(
        mode(destination.join("assets").join("readme.txt")),
        READABLE
    );
}

#[cfg(unix)]
#[test]
fn ignore_declared_modes_writes_regular_files() {
    use std::os::unix::fs::PermissionsExt;
    let root = TempDir::new("zip-ignore-modes");
    let archive = create_zip(
        &[
            ("plugin.dll", b"plugin", Some(0x81ED)),
            ("assets/readme.txt", b"readme", Some(0x81A4)),
        ],
        Compression::Stored,
    );
    let zip = write_archive(&root, "ignore-modes.zip", &archive);
    let destination = root.join("ignore-modes");
    let files = SafeZipExtractor::new(SafeZipExtractionLimits::default(), true)
        .extract(&zip, &destination, &[], &none())
        .unwrap();
    let mode = |path: PathBuf| std::fs::metadata(path).unwrap().permissions().mode() & 0o777;
    assert_eq!(mode(destination.join("plugin.dll")), READABLE);
    assert_eq!(mode_of(&files, "plugin.dll"), 0o644);
}

#[test]
fn ignore_declared_modes_rejects_executable_names() {
    let root = TempDir::new("zip-ignore-conflict");
    let archive = create_zip(
        &[("plugin.dll", b"plugin", Some(0x81ED))],
        Compression::Stored,
    );
    let zip = write_archive(&root, "conflict.zip", &archive);
    assert!(
        SafeZipExtractor::new(SafeZipExtractionLimits::default(), true)
            .extract(
                &zip,
                &root.join("conflict"),
                &["plugin.dll".to_owned()],
                &none()
            )
            .is_err()
    );
}

#[test]
fn declared_unix_modes_still_win_for_every_non_executable_entry() {
    let root = TempDir::new("zip-declared");
    let archive = create_zip(
        &[
            ("openac-launcher", b"launcher", Some(0x81A4)),
            ("support.dat", b"support", Some(0x8180)),
            ("tools/helper", b"helper", Some(0x81ED)),
        ],
        Compression::Stored,
    );
    let zip = write_archive(&root, "declared.zip", &archive);
    let (extractor, applied) = recording_extractor(false);
    let files = extractor
        .extract(
            &zip,
            &root.join("declared"),
            &executables_for_payload("linux-x64", true),
            &none(),
        )
        .unwrap();
    assert_eq!(applied_mode(&applied, "support.dat"), 0o600);
    assert_eq!(applied_mode(&applied, "helper"), EXECUTABLE);
    assert_eq!(applied_mode(&applied, "openac-launcher"), EXECUTABLE);
    assert_eq!(mode_of(&files, "support.dat"), 0o600);
    assert_eq!(mode_of(&files, "tools/helper"), 0o755);
    assert_eq!(mode_of(&files, "openac-launcher"), 0o755);
}

#[test]
fn an_absent_executable_name_is_not_an_extraction_error() {
    let root = TempDir::new("zip-absent");
    let archive = create_zip(
        &[("openac-launcher", b"launcher", None)],
        Compression::Stored,
    );
    let zip = write_archive(&root, "absent.zip", &archive);
    let (extractor, _) = recording_extractor(false);
    let files = extractor
        .extract(
            &zip,
            &root.join("absent"),
            &executables_for_payload("linux-x64", true),
            &none(),
        )
        .unwrap();
    assert_eq!(files.len(), 1);
    assert_eq!(mode_of(&files, "openac-launcher"), 0o755);
}

#[test]
fn rejects_traversal_rooted_ads_and_portable_unsafe_names() {
    for entry in [
        "../escape",
        "a/../../escape",
        "/rooted",
        "C:/drive",
        "file:stream",
        "a//b",
        "a/./b",
        "CON",
        "aux.txt",
        "CLOCK$/value",
        "CONIN$.txt",
        "CONOUT$/value",
        "COM\u{b9}.dll",
        "com\u{b2}/value",
        "LPT\u{b3}.log",
        "trailing.",
        "trailing ",
    ] {
        let root = TempDir::new("zip-unsafe");
        let zip = write_archive(
            &root,
            "unsafe.zip",
            &create_zip(&[(entry, b"bad", Some(0x81A4))], Compression::Stored),
        );
        let destination = root.join("unsafe");
        assert!(
            matches!(
                SafeZipExtractor::default().extract(&zip, &destination, &[], &none()),
                Err(UpdateError::Failed { .. })
            ),
            "{entry}"
        );
        assert!(!destination.exists(), "{entry}");
        assert!(!root.join("escape").exists(), "{entry}");
    }
}

#[test]
fn rejects_duplicate_case_and_file_directory_collisions_before_extraction() {
    let archives = [
        create_zip(
            &[
                ("Readme.txt", b"a", Some(0x81A4)),
                ("README.TXT", b"b", Some(0x81A4)),
            ],
            Compression::Stored,
        ),
        create_zip(
            &[
                ("node", b"file", Some(0x81A4)),
                ("node/child", b"child", Some(0x81A4)),
            ],
            Compression::Stored,
        ),
        create_zip(
            &[
                ("Folder/one", b"one", Some(0x81A4)),
                ("folder/two", b"two", Some(0x81A4)),
            ],
            Compression::Stored,
        ),
    ];
    for archive in archives {
        let root = TempDir::new("zip-collide");
        let zip = write_archive(&root, "collide.zip", &archive);
        let destination = root.join("collide");
        assert!(
            SafeZipExtractor::default()
                .extract(&zip, &destination, &[], &none())
                .is_err()
        );
        assert!(!destination.exists());
    }
}

#[test]
fn rejects_symlink_and_reparse_metadata() {
    let root = TempDir::new("zip-symlink");
    let zip = write_archive(
        &root,
        "symlink.zip",
        &create_zip(
            &[("link", b"../../outside", Some(0xA1FF))],
            Compression::Stored,
        ),
    );
    let destination = root.join("symlink");
    let error = SafeZipExtractor::default()
        .extract(&zip, &destination, &[], &none())
        .unwrap_err()
        .to_string();
    assert!(error.to_lowercase().contains("symlink"), "{error}");
    assert!(!destination.exists());
}

#[test]
fn rejects_entry_count_size_total_and_compression_ratio_bombs() {
    let defaults = SafeZipExtractionLimits::default();
    let cases = [
        (
            create_zip(
                &[("one", &[1], Some(0x81A4)), ("two", &[2], Some(0x81A4))],
                Compression::Stored,
            ),
            SafeZipExtractionLimits {
                maximum_entries: 1,
                ..defaults
            },
        ),
        (
            create_zip(&[("large", &[0; 8], Some(0x81A4))], Compression::Stored),
            SafeZipExtractionLimits {
                maximum_entry_bytes: 7,
                ..defaults
            },
        ),
        (
            create_zip(
                &[
                    ("one", &[0; 6], Some(0x81A4)),
                    ("two", &[0; 6], Some(0x81A4)),
                ],
                Compression::Stored,
            ),
            SafeZipExtractionLimits {
                maximum_total_bytes: 10,
                ..defaults
            },
        ),
        (
            create_zip(
                &[("ratio", &vec![0; 64 * 1024], Some(0x81A4))],
                Compression::Smallest,
            ),
            SafeZipExtractionLimits {
                maximum_compression_ratio: 2.0,
                ..defaults
            },
        ),
    ];
    for (archive, limits) in cases {
        let root = TempDir::new("zip-bomb");
        let zip = write_archive(&root, "bomb.zip", &archive);
        let destination = root.join("bomb");
        assert!(
            SafeZipExtractor::new(limits, false)
                .extract(&zip, &destination, &[], &none())
                .is_err()
        );
        assert!(!destination.exists());
    }
}

#[test]
fn existing_non_empty_destination_and_cancellation_never_publish_partial_tree() {
    let root = TempDir::new("zip-cancel");
    let zip = write_archive(
        &root,
        "cancel.zip",
        &create_zip(
            &[("large", &vec![0; 1024 * 1024], Some(0x81A4))],
            Compression::Stored,
        ),
    );
    let non_empty = root.join("nonempty");
    std::fs::create_dir_all(&non_empty).unwrap();
    std::fs::write(non_empty.join("owner"), "preserve").unwrap();
    assert!(
        SafeZipExtractor::default()
            .extract(&zip, &non_empty, &[], &none())
            .is_err()
    );
    assert_eq!(
        std::fs::read_to_string(non_empty.join("owner")).unwrap(),
        "preserve"
    );

    let cancelled = root.join("cancelled");
    let cancel = CancelToken::new();
    cancel.cancel();
    let result = SafeZipExtractor::default().extract(&zip, &cancelled, &[], &cancel);
    assert!(result.unwrap_err().is_cancelled());
    assert!(!cancelled.exists());
}

// --- UpdateSessionBarrierTests ------------------------------------------------

#[test]
fn shared_sessions_coexist_and_exclude_update_transactions() {
    let root = TempDir::new("lease");
    let barrier = UpdateSessionBarrier::new(&root.path);
    let _first = barrier.acquire_session().unwrap();
    let _second = barrier.acquire_session().unwrap();
    let blocked = barrier.acquire_exclusive().unwrap_err().to_string();
    assert!(blocked.to_lowercase().contains("session"), "{blocked}");
}

#[test]
fn exclusive_updater_excludes_sessions_and_concurrent_updater() {
    let root = TempDir::new("lease");
    let barrier = UpdateSessionBarrier::new(&root.path);
    let _update = barrier.acquire_exclusive().unwrap();
    assert!(barrier.acquire_session().is_err());
    assert!(barrier.acquire_exclusive().is_err());
}

// --- ClientVersionStoreTests ----------------------------------------------------

struct StoreFixture {
    root: TempDir,
    rid: String,
}

impl StoreFixture {
    fn new() -> Self {
        Self {
            root: TempDir::new("client-version"),
            rid: rid(),
        }
    }

    fn store(&self) -> ClientVersionStore {
        ClientVersionStore::new(&self.root.path_set())
    }

    fn promote(
        &self,
        store: &ClientVersionStore,
        version: &str,
        marker: &str,
    ) -> Result<ClientVersionResolution, UpdateError> {
        let archive = client_zip(&self.rid, marker);
        let zip = self.root.join(format!("{version}-{marker}.zip"));
        std::fs::write(&zip, &archive).unwrap();
        let staging = store.create_client_staging_directory().unwrap();
        let files = SafeZipExtractor::default()
            .extract(&zip, &staging, &[], &none())
            .unwrap();
        let _lease = store.barrier().acquire_exclusive().unwrap();
        store.promote_and_activate_under_lease(
            &staging,
            &LauncherVersion::try_parse(version).unwrap(),
            &self.rid,
            &artifact(
                &format!("https://example.test/{version}.zip"),
                &sha256_hex(&archive),
                archive.len() as u64,
            ),
            &files,
            &none(),
        )
    }
}

#[test]
fn dynamic_executable_resolver_tracks_only_verified_current_version() {
    use ac_launcher_core::orchestration::LauncherExecutableSet;
    use ac_launcher_core::profiles::LaunchMode;

    let fixture = StoreFixture::new();
    let store = Arc::new(fixture.store());
    let executables = LauncherExecutableSet::from_current_version_store(Arc::clone(&store));
    assert!(!executables.availability(LaunchMode::Gui).is_available);

    let program = format!(
        "openac{}",
        ac_launcher_core::updates::suffix_for_rid(&fixture.rid)
    );
    let session = Path::new("session.json");
    let first = fixture.promote(&store, "1.0.0", "one").unwrap();
    assert_eq!(
        executables.paths().unwrap().working_directory,
        first.directory
    );
    assert_eq!(
        executables
            .create_play_spec(LaunchMode::Gui, session, None)
            .unwrap()
            .executable_path,
        first.directory.as_ref().unwrap().join(&program)
    );

    let second = fixture.promote(&store, "2.0.0", "two").unwrap();
    assert_eq!(
        executables.paths().unwrap().working_directory,
        second.directory
    );
    assert_eq!(
        executables
            .create_probe_spec(session, None)
            .unwrap()
            .executable_path,
        second.directory.as_ref().unwrap().join(&program)
    );
}

// A client released before the install's current folder layout (0.1.9 was
// the last) would read and write the old folders, so it is never started
// and never restored.
#[test]
fn a_client_older_than_the_folder_layout_is_not_started() {
    use ac_launcher_core::orchestration::LauncherExecutableSet;
    use ac_launcher_core::profiles::LaunchMode;

    let fixture = StoreFixture::new();
    let store = Arc::new(fixture.store());
    fixture.promote(&store, "0.1.9", "old").unwrap();
    for executables in [
        LauncherExecutableSet::from_current_version_store(Arc::clone(&store)),
        LauncherExecutableSet::from_current_version_store_or_directory(
            Arc::clone(&store),
            fixture.root.path.as_path(),
        ),
    ] {
        let capability = executables.availability(LaunchMode::Gui);
        assert!(!capability.is_available);
        assert!(
            capability
                .reason
                .as_deref()
                .is_some_and(|reason| reason.contains("Update the client")),
            "{:?}",
            capability.reason
        );
    }
    fixture.promote(&store, "0.1.10", "new").unwrap();
    let executables = LauncherExecutableSet::from_current_version_store(Arc::clone(&store));
    assert!(executables.availability(LaunchMode::Gui).is_available);
}

#[test]
fn an_old_store_client_gives_way_to_the_client_beside_the_launcher() {
    use ac_launcher_core::orchestration::LauncherExecutableSet;
    use ac_launcher_core::profiles::LaunchMode;

    let fixture = StoreFixture::new();
    let store = Arc::new(fixture.store());
    fixture.promote(&store, "0.1.9", "old").unwrap();
    // A launcher built with its client beside it (the same build).
    let beside = fixture.root.join("launcher");
    std::fs::create_dir_all(&beside).unwrap();
    let program = format!(
        "openac{}",
        ac_launcher_core::updates::suffix_for_rid(&fixture.rid)
    );
    std::fs::write(beside.join(&program), "client").unwrap();
    let executables =
        LauncherExecutableSet::from_current_version_store_or_directory(Arc::clone(&store), &beside)
            .with_unix_execute_permission(Arc::new(|_| true));
    let capability = executables.availability(LaunchMode::Gui);
    assert!(capability.is_available, "{:?}", capability.reason);
    assert_eq!(
        executables.paths().unwrap().graphical_host_path,
        beside.join(&program)
    );
}

#[test]
fn the_old_client_message_reads_as_one_line() {
    let version = LauncherVersion::try_parse("0.1.9").unwrap();
    assert!(ac_launcher_core::updates::predates_the_folder_layout(
        &version
    ));
    assert!(!ac_launcher_core::updates::predates_the_folder_layout(
        &LauncherVersion::try_parse("0.1.10-beta.1").unwrap()
    ));
    assert_eq!(
        ac_launcher_core::updates::predates_the_folder_layout_message(&version),
        "Client 0.1.9 was made for the old folder layout and cannot run on this install. \
         Update the client."
    );
}

#[test]
fn a_client_older_than_the_folder_layout_is_never_rolled_back_to() {
    let fixture = StoreFixture::new();
    let store = fixture.store();
    fixture.promote(&store, "0.1.9", "old").unwrap();
    fixture.promote(&store, "0.1.10", "new").unwrap();

    let error = store.rollback(&fixture.rid, &none()).unwrap_err();
    assert!(error.to_string().contains("Update the client"), "{error}");
    assert_eq!(store.cached_resolution().version.unwrap().value(), "0.1.10");

    // Nor restored when the active pointer is lost and only the previous
    // one is left.
    std::fs::remove_file(store.current_pointer_path()).unwrap();
    let recovered = fixture
        .store()
        .load_and_recover(&fixture.rid, &none())
        .unwrap();
    assert!(!recovered.is_verified(), "{}", recovered.status);
    assert!(
        recovered.status.contains("Update the client"),
        "{}",
        recovered.status
    );
}

#[test]
fn updates_reuse_one_client_path_and_retain_only_one_rollback_copy() {
    let fixture = StoreFixture::new();
    let store = fixture.store();
    let directory = fixture
        .promote(&store, "1.0.0", "first")
        .unwrap()
        .directory
        .unwrap();
    assert_eq!(directory, store.app_directory().join("client"));
    for version in ["2.0.0", "3.0.0", "4.0.0"] {
        assert_eq!(
            fixture.promote(&store, version, version).unwrap().directory,
            Some(directory.clone())
        );
    }
    let mut folders: Vec<String> = std::fs::read_dir(store.app_directory())
        .unwrap()
        .flatten()
        .filter(|entry| entry.path().is_dir())
        .map(|entry| entry.file_name().to_string_lossy().into_owned())
        .collect();
    folders.sort();
    assert_eq!(folders, ["client", "client-previous"]);
    assert_eq!(
        store.rollback(&fixture.rid, &none()).unwrap().directory,
        Some(directory.clone())
    );
    assert_eq!(store.cached_resolution().version.unwrap().value(), "3.0.0");
    assert_eq!(
        store.rollback(&fixture.rid, &none()).unwrap().directory,
        Some(directory)
    );
    assert_eq!(store.cached_resolution().version.unwrap().value(), "4.0.0");
}

#[test]
fn legacy_version_folder_migrates_during_update_without_touching_user_data() {
    let fixture = StoreFixture::new();
    let store = fixture.store();
    let first = fixture.promote(&store, "1.0.0", "first").unwrap();
    let legacy = store.app_directory().join("1.0.0");
    std::fs::rename(first.directory.unwrap(), &legacy).unwrap();
    let user_file = fixture.root.join("profile.usd");
    std::fs::write(&user_file, "personal items").unwrap();
    assert_eq!(
        store
            .load_and_recover(&fixture.rid, &none())
            .unwrap()
            .directory,
        Some(legacy.clone())
    );
    assert_eq!(
        fixture
            .promote(&store, "2.0.0", "second")
            .unwrap()
            .directory,
        Some(store.client_directory())
    );
    assert!(!legacy.exists());
    assert_eq!(
        std::fs::read_to_string(&user_file).unwrap(),
        "personal items"
    );
    assert_eq!(
        store
            .rollback(&fixture.rid, &none())
            .unwrap()
            .version
            .unwrap()
            .value(),
        "1.0.0"
    );
}

/// OpenAC `StartupCompletesInterruptedReplacementAtEveryDirectoryBoundary`
/// (stages 0 to 4): a rollback journal written, then each directory move
/// and the pointer write done before the crash.
#[test]
fn startup_completes_interrupted_replacement_at_every_directory_boundary() {
    for stage in 0..=4 {
        let fixture = StoreFixture::new();
        let store = fixture.store();
        fixture.promote(&store, "1.0.0", "first").unwrap();
        fixture.promote(&store, "2.0.0", "second").unwrap();
        let journal = store.app_directory().join(".client-replacement.json");
        let pointer =
            "{\"schemaVersion\":1,\"currentVersion\":\"1.0.0\",\"previousVersion\":\"2.0.0\"}";
        std::fs::write(
            &journal,
            format!(
                "{{\"incomingName\":\"client-previous\",\"oldName\":\"client\",\"pointer\":{pointer},\"keepPrevious\":false}}"
            ),
        )
        .unwrap();
        let replaced = store.app_directory().join(".client-replaced");
        if stage >= 1 {
            std::fs::rename(store.client_directory(), &replaced).unwrap();
        }
        if stage >= 2 {
            std::fs::rename(store.backup_directory(), store.client_directory()).unwrap();
        }
        if stage >= 3 {
            std::fs::write(store.current_pointer_path(), pointer).unwrap();
        }
        if stage >= 4 {
            std::fs::rename(&replaced, store.backup_directory()).unwrap();
        }
        assert_eq!(
            store
                .load_current_read_only(&fixture.rid, &none())
                .unwrap()
                .state,
            ClientVersionState::Invalid,
            "stage {stage}"
        );
        assert!(!store.cached_resolution().is_verified());
        let result = fixture
            .store()
            .load_and_recover(&fixture.rid, &none())
            .unwrap();
        assert!(result.is_verified(), "stage {stage}: {}", result.status);
        assert_eq!(result.version.as_ref().unwrap().value(), "1.0.0");
        assert_eq!(result.directory, Some(store.client_directory()));
        assert_eq!(
            store
                .rollback(&fixture.rid, &none())
                .unwrap()
                .version
                .unwrap()
                .value(),
            "2.0.0"
        );
        assert!(!journal.exists());
    }
}

#[test]
fn atomic_promotion_publishes_strict_pointer_and_retains_previous_for_rollback() {
    let fixture = StoreFixture::new();
    let store = fixture.store();
    let first = fixture.promote(&store, "1.0.0", "first").unwrap();
    let second = fixture.promote(&store, "2.0.0", "second").unwrap();
    assert!(first.is_verified());
    assert!(second.is_verified());
    assert_eq!(second.version.as_ref().unwrap().value(), "2.0.0");
    assert_eq!(second.previous_version.as_deref(), Some("1.0.0"));
    for version in ["1.0.0", "2.0.0"] {
        assert!(
            store
                .version_directory(&LauncherVersion::try_parse(version).unwrap())
                .is_dir()
        );
    }
    assert!(
        !std::fs::read_dir(store.app_directory())
            .unwrap()
            .flatten()
            .any(|entry| entry
                .file_name()
                .to_string_lossy()
                .starts_with(".client-staging-"))
    );
    let pointer = std::fs::read_to_string(store.current_pointer_path()).unwrap();
    assert!(pointer.contains("\"schemaVersion\": 1"));
    assert!(pointer.contains("\"currentVersion\": \"2.0.0\""));
    assert!(!pointer.contains(".client-staging"));

    let rolled_back = store.rollback(&fixture.rid, &none()).unwrap();
    assert_eq!(rolled_back.version.as_ref().unwrap().value(), "1.0.0");
    assert_eq!(rolled_back.previous_version.as_deref(), Some("2.0.0"));
    assert_eq!(
        std::fs::read_to_string(
            rolled_back
                .directory
                .as_ref()
                .unwrap()
                .join(program(&fixture.rid))
        )
        .unwrap(),
        "first-gui"
    );
}

#[test]
fn torn_current_pointer_recovers_last_durable_pointer_and_owned_temp_residue() {
    let fixture = StoreFixture::new();
    let store = fixture.store();
    fixture.promote(&store, "1.0.0", "first").unwrap();
    fixture.promote(&store, "2.0.0", "second").unwrap();
    let temp = store.app_directory().join(format!(
        ".current.json.{}.tmp",
        ac_launcher_core::ids::unique_hex32()
    ));
    std::fs::write(&temp, "partial temp").unwrap();
    std::fs::write(store.current_pointer_path(), "{\"schemaVersion\":1,").unwrap();

    let recovered_store = fixture.store();
    let recovered = recovered_store
        .load_and_recover(&fixture.rid, &none())
        .unwrap();
    assert!(recovered.is_verified());
    assert_eq!(recovered.version.as_ref().unwrap().value(), "1.0.0");
    assert!(recovered.status.contains("Recovered"));
    assert!(!temp.exists());
    assert!(
        std::fs::read_to_string(recovered_store.current_pointer_path())
            .unwrap()
            .contains("\"currentVersion\": \"1.0.0\"")
    );
}

#[test]
fn corrupt_active_install_fails_closed_but_verified_previous_can_rollback() {
    let fixture = StoreFixture::new();
    let store = fixture.store();
    fixture.promote(&store, "1.0.0", "first").unwrap();
    let second = fixture.promote(&store, "2.0.0", "second").unwrap();
    std::fs::write(
        second
            .directory
            .as_ref()
            .unwrap()
            .join(program(&fixture.rid)),
        "tampered!!",
    )
    .unwrap();

    let restarted = fixture.store();
    let invalid = restarted.load_and_recover(&fixture.rid, &none()).unwrap();
    assert_eq!(invalid.state, ClientVersionState::Invalid);
    assert!(
        invalid.status.to_lowercase().contains("corrupt"),
        "{}",
        invalid.status
    );
    let rolled_back = restarted.rollback(&fixture.rid, &none()).unwrap();
    assert_eq!(rolled_back.version.as_ref().unwrap().value(), "1.0.0");
}

#[test]
fn strict_pointer_and_install_records_reject_unknown_unrecorded_and_wrong_rid_state() {
    let fixture = StoreFixture::new();
    let store = fixture.store();
    let installed = fixture.promote(&store, "1.0.0", "strict").unwrap();
    let directory = installed.directory.clone().unwrap();
    std::fs::write(directory.join("unrecorded.dll"), "unexpected").unwrap();

    let restarted = fixture.store();
    let unrecorded = restarted.load_and_recover(&fixture.rid, &none()).unwrap();
    assert_eq!(unrecorded.state, ClientVersionState::Invalid);
    assert!(unrecorded.status.to_lowercase().contains("unrecorded"));

    std::fs::remove_file(directory.join("unrecorded.dll")).unwrap();
    let install_path = ClientVersionStore::metadata_path(&directory);
    let install = std::fs::read_to_string(&install_path).unwrap();
    std::fs::write(
        &install_path,
        install.replace(
            "\"schemaVersion\": 1",
            "\"schemaVersion\": 1,\"schemaVersion\": 1",
        ),
    )
    .unwrap();
    let duplicate = restarted.load_and_recover(&fixture.rid, &none()).unwrap();
    assert_eq!(duplicate.state, ClientVersionState::Invalid);
    assert!(
        duplicate.status.contains("Duplicate"),
        "{}",
        duplicate.status
    );
    std::fs::write(&install_path, &install).unwrap();

    let current = std::fs::read_to_string(store.current_pointer_path()).unwrap();
    std::fs::write(
        store.current_pointer_path(),
        format!(
            "{},\"unknown\":true}}",
            current.trim_end().trim_end_matches('}')
        ),
    )
    .unwrap();
    let unknown = restarted.load_and_recover(&fixture.rid, &none()).unwrap();
    assert_eq!(unknown.state, ClientVersionState::Invalid);

    std::fs::write(store.current_pointer_path(), &current).unwrap();
    let other_rid = if fixture.rid == "win-x64" {
        "linux-x64"
    } else {
        "win-x64"
    };
    let wrong_rid = restarted.load_and_recover(other_rid, &none()).unwrap();
    assert_eq!(wrong_rid.state, ClientVersionState::Invalid);
    assert!(wrong_rid.status.contains("RID") || wrong_rid.status.contains("missing"));
}

#[test]
fn existing_semantic_version_cannot_replace_active_content_in_place() {
    let fixture = StoreFixture::new();
    let store = fixture.store();
    fixture.promote(&store, "1.0.0", "original").unwrap();
    let error = fixture
        .promote(&store, "1.0.0", "different")
        .unwrap_err()
        .to_string();
    assert!(
        error.to_lowercase().contains("active client version"),
        "{error}"
    );
    let resolution = store.load_and_recover(&fixture.rid, &none()).unwrap();
    assert_eq!(
        std::fs::read_to_string(
            resolution
                .directory
                .as_ref()
                .unwrap()
                .join(program(&fixture.rid))
        )
        .unwrap(),
        "original-gui"
    );
}

#[cfg(target_os = "linux")]
#[test]
fn unix_rejects_non_canonical_install_json_casing() {
    let fixture = StoreFixture::new();
    let store = fixture.store();
    let installed = fixture.promote(&store, "1.0.0", "linux-case").unwrap();
    std::fs::write(
        installed.directory.as_ref().unwrap().join("INSTALL.JSON"),
        "must-not-be-hidden",
    )
    .unwrap();
    let resolution = fixture
        .store()
        .load_and_recover(&fixture.rid, &none())
        .unwrap();
    assert_eq!(resolution.state, ClientVersionState::Invalid);
    assert!(resolution.status.to_lowercase().contains("unrecorded"));
}

#[test]
fn exclusive_startup_reclaims_only_canonical_owned_residue() {
    let fixture = StoreFixture::new();
    let store = fixture.store();
    std::fs::create_dir_all(store.app_directory()).unwrap();
    let id = ac_launcher_core::ids::unique_hex32();
    let near_id = format!("{}g", &id[..31]);
    let app = store.app_directory();
    let exact_staging = app.join(format!(".client-staging-{id}"));
    let exact_corrupt = app.join(format!(".client-corrupt-{id}"));
    let exact_download = app.join(format!(".client-download-{id}.zip"));
    let near_staging = app.join(format!(".client-staging-{id}-user"));
    let near_corrupt = app.join(format!(".client-corrupt-{near_id}"));
    let near_download = app.join(format!(".client-download-{id}.zip.user"));
    for directory in [&exact_staging, &exact_corrupt, &near_staging, &near_corrupt] {
        std::fs::create_dir_all(directory).unwrap();
    }
    std::fs::write(&exact_download, "owned").unwrap();
    std::fs::write(&near_download, "preserve").unwrap();

    store.load_and_recover(&fixture.rid, &none()).unwrap();

    assert!(!exact_staging.exists());
    assert!(!exact_corrupt.exists());
    assert!(!exact_download.exists());
    assert!(near_staging.exists());
    assert!(near_corrupt.exists());
    assert!(near_download.exists());
}

// --- LauncherUpdaterIntegrationTests -------------------------------------------

fn updater(
    root: &TempDir,
    server: &FakeServer,
    rid: &str,
    running: impl Fn() -> bool + Send + Sync + 'static,
) -> (LauncherUpdater, Arc<ClientVersionStore>) {
    let versions = Arc::new(ClientVersionStore::new(&root.path_set()));
    let updater = LauncherUpdater::new(
        Arc::new(loopback_client(server)),
        server.transport(),
        Arc::clone(&versions),
        LauncherVersion::try_parse("1.0.0").unwrap(),
        rid,
        running,
    )
    .unwrap();
    (updater, versions)
}

#[test]
fn local_http_manifest_download_extract_promotion_and_next_check_are_coherent() {
    let root = TempDir::new("updater");
    let rid = rid();
    let server = FakeServer::new();
    configure_release(
        &server,
        "2.0.0",
        "1.0.0",
        &rid,
        &client_zip(&rid, "release-2"),
        &launcher_zip(&rid, "release-2"),
    );
    let (updater, versions) = updater(&root, &server, &rid, || false);
    updater.initialize(&none()).unwrap();
    let mut progress = Vec::new();

    let check = updater.check(&none()).unwrap();
    let installed = updater
        .install_client(&check, &mut |value| progress.push(value), &none())
        .unwrap();
    let after = updater.check(&none()).unwrap();

    assert!(check.is_client_update_available);
    assert!(check.is_launcher_update_available);
    assert!(check.is_launcher_minimum_satisfied);
    assert!(installed.is_verified());
    assert_eq!(installed.version.as_ref().unwrap().value(), "2.0.0");
    assert!(!after.is_client_update_available);
    assert!(after.is_launcher_update_available);
    for phase in [
        LauncherUpdatePhase::DownloadingClient,
        LauncherUpdatePhase::ExtractingClient,
        LauncherUpdatePhase::ActivatingClient,
    ] {
        assert!(progress.iter().any(|item| item.phase == phase), "{phase:?}");
    }
    assert_eq!(
        progress.last().unwrap().phase,
        LauncherUpdatePhase::Completed
    );
    assert!(
        !std::fs::read_dir(versions.app_directory())
            .unwrap()
            .flatten()
            .any(|entry| entry
                .file_name()
                .to_string_lossy()
                .starts_with(".client-download-"))
    );
}

#[test]
fn minimum_launcher_gate_and_missing_rid_fail_before_publication() {
    let root = TempDir::new("updater-minimum");
    let rid = rid();
    let server = FakeServer::new();
    configure_release(
        &server,
        "3.0.0",
        "2.0.0",
        &rid,
        &client_zip(&rid, "client"),
        &launcher_zip(&rid, "launcher"),
    );
    let (updater, versions) = updater(&root, &server, &rid, || false);
    updater.initialize(&none()).unwrap();
    let check = updater.check(&none()).unwrap();
    assert!(!check.is_launcher_minimum_satisfied);
    assert!(
        updater
            .install_client(&check, &mut |_| {}, &none())
            .is_err()
    );
    assert!(!versions.current_pointer_path().exists());

    let other = if rid == "win-x64" {
        "linux-x64"
    } else {
        "win-x64"
    };
    configure_release(
        &server,
        "3.0.0",
        "1.0.0",
        other,
        &client_zip(other, "client"),
        &launcher_zip(other, "launcher"),
    );
    let error = updater.check(&none()).unwrap_err().to_string();
    assert!(error.contains(&rid), "{error}");
}

#[test]
fn running_predicate_and_cross_process_barrier_refuse_update_without_network_mutation() {
    let root = TempDir::new("updater-running");
    let rid = rid();
    let server = FakeServer::new();
    configure_release(
        &server,
        "2.0.0",
        "1.0.0",
        &rid,
        &client_zip(&rid, "client"),
        &launcher_zip(&rid, "launcher"),
    );
    let running = Arc::new(AtomicBool::new(true));
    let flag = Arc::clone(&running);
    let (updater, versions) = updater(&root, &server, &rid, move || flag.load(Ordering::SeqCst));
    updater.initialize(&none()).unwrap();
    let check = updater.check(&none()).unwrap();

    let local = updater
        .install_client(&check, &mut |_| {}, &none())
        .unwrap_err()
        .to_string();
    assert!(local.contains("Stop every"), "{local}");
    assert!(!versions.current_pointer_path().exists());

    running.store(false, Ordering::SeqCst);
    let _session = versions.barrier().acquire_session().unwrap();
    let shared = updater
        .install_client(&check, &mut |_| {}, &none())
        .unwrap_err()
        .to_string();
    assert!(shared.to_lowercase().contains("session"), "{shared}");
    assert!(!versions.current_pointer_path().exists());
}

#[test]
fn cancelled_slow_client_download_leaves_no_pointer_or_staging() {
    let root = TempDir::new("updater-cancel");
    let rid = rid();
    let server = FakeServer::new();
    let client = client_zip(&rid, &"x".repeat(1_000_000));
    let launcher = launcher_zip(&rid, "launcher");
    let client_url = FakeServer::url("client.zip");
    let launcher_url = FakeServer::url("launcher.zip");
    server.add_slow(&client_url, &client, 1024, Duration::from_millis(2));
    server.add(&launcher_url, &launcher);
    server.add(
        &FakeServer::url("manifest.json"),
        &fixtures::manifest(
            "2.0.0",
            "1.0.0",
            &rid,
            &client_url,
            &client,
            &launcher_url,
            &launcher,
        ),
    );
    let (updater, versions) = updater(&root, &server, &rid, || false);
    updater.initialize(&none()).unwrap();
    let check = updater.check(&none()).unwrap();
    let cancel = CancelToken::new();
    let canceller = cancel.clone();
    let timer = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(30));
        canceller.cancel();
    });
    let result = updater.install_client(&check, &mut |_| {}, &cancel);
    timer.join().unwrap();
    assert!(result.unwrap_err().is_cancelled());
    assert!(!versions.current_pointer_path().exists());
    assert!(
        !std::fs::read_dir(versions.app_directory())
            .unwrap()
            .flatten()
            .any(|entry| entry.file_name().to_string_lossy().starts_with(".client-"))
    );
}

// --- LauncherFingerprintTests -----------------------------------------------------------
// OpenAC `tests/AcDream.Launcher.Core.Tests/Updates/LauncherFingerprintTests.cs`: a launcher
// updates itself only when the release's launcher differs from it; a launcher from before
// fingerprints, or a release without one, still updates by version.

mod fingerprint {
    use std::sync::Arc;
    use std::time::{Duration, Instant};

    use ac_launcher_core::cancel::CancelToken;
    use ac_launcher_core::net::{HttpError, HttpRequest, HttpResponse, HttpTransport};
    use ac_launcher_core::updates::{
        ClientVersionStore, LAUNCHER_FINGERPRINT_FILE_NAME, LauncherFingerprintDocument,
        LauncherUpdater, LauncherVersion, ReleaseArtifact, ReleaseManifest, ReleaseManifestClient,
        ReleaseManifestSource, UpdateError, decide_launcher,
    };
    use ac_launcher_core::url::WebUrl;

    use super::common::TempDir;
    use super::common::fixtures::{FakeServer, client_zip, launcher_zip};
    use super::{configure_release, loopback_client, none, rid};

    fn same() -> String {
        "a".repeat(64)
    }

    fn other() -> String {
        "b".repeat(64)
    }

    fn version(text: &str) -> LauncherVersion {
        LauncherVersion::try_parse(text).unwrap()
    }

    fn manifest(release: &str, minimum: &str) -> ReleaseManifest {
        let artifact = ReleaseArtifact {
            url: WebUrl::parse("https://example.test/a.zip").unwrap(),
            sha256: same(),
            size: 1,
        };
        ReleaseManifest {
            version: version(release),
            minimum_launcher_version: version(minimum),
            clients: [("win-x64".to_owned(), artifact.clone())].into(),
            launchers: [("win-x64".to_owned(), artifact)].into(),
        }
    }

    /// Running version, running fingerprint, published release and fingerprint, then
    /// whether the launcher updates and whether its minimum is met.
    type DecisionCase = (
        &'static str,
        Option<String>,
        Option<(&'static str, String)>,
        bool,
        bool,
    );

    #[test]
    fn the_fingerprint_decides_when_both_are_known_for_this_release() {
        // running version, running fingerprint, published release and fingerprint → update?,
        // minimum met?
        let cases: [DecisionCase; 7] = [
            (
                "0.1.20",
                Some(same()),
                Some(("0.1.21", same())),
                false,
                true,
            ),
            (
                "0.1.20",
                Some(same().to_uppercase()),
                Some(("0.1.21", same())),
                false,
                true,
            ),
            (
                "0.1.20",
                Some(same()),
                Some(("0.1.21", other())),
                true,
                false,
            ),
            (
                "0.1.22",
                Some(same()),
                Some(("0.1.21", other())),
                false,
                true,
            ),
            ("0.1.20", None, Some(("0.1.21", same())), true, false),
            ("0.1.20", Some(same()), None, true, false),
            (
                "0.1.20",
                Some(same()),
                Some(("0.1.19", same())),
                true,
                false,
            ),
        ];
        for (running, running_fingerprint, published, update, minimum) in cases {
            let published = published.map(|(release, fingerprint)| LauncherFingerprintDocument {
                version: version(release),
                fingerprint,
            });
            assert_eq!(
                decide_launcher(
                    &manifest("0.1.21", "0.1.21"),
                    &version(running),
                    running_fingerprint.as_deref(),
                    published.as_ref(),
                ),
                (update, minimum),
                "{running} {running_fingerprint:?} {published:?}"
            );
        }
    }

    #[test]
    fn only_a_well_formed_fingerprint_is_read() {
        let upper = "A".repeat(64);
        let lower = same();
        let valid = format!(r#"{{"schemaVersion":1,"version":"0.1.21","fingerprint":"{upper}"}}"#);
        let document = LauncherFingerprintDocument::parse(valid.as_bytes()).unwrap();
        assert_eq!(
            (document.version, document.fingerprint),
            (version("0.1.21"), same())
        );
        for invalid in [
            format!(r#"{{"schemaVersion":2,"version":"0.1.21","fingerprint":"{lower}"}}"#),
            r#"{"schemaVersion":1,"version":"0.1.21","fingerprint":"not-hex"}"#.to_owned(),
            format!(r#"{{"schemaVersion":1,"version":"latest","fingerprint":"{lower}"}}"#),
            format!(
                r#"{{"schemaVersion":1,"version":"0.1.21","fingerprint":"{lower}","extra":1}}"#
            ),
        ] {
            assert!(
                LauncherFingerprintDocument::parse(invalid.as_bytes()).is_err(),
                "{invalid}"
            );
        }
    }

    /// Answers every request with one failure: a timeout, a request that
    /// hangs until its timeout or the caller's stop, or a reset.
    struct Failing(&'static str);

    impl HttpTransport for Failing {
        fn get(
            &self,
            request: &HttpRequest,
            cancel: &CancelToken,
        ) -> Result<HttpResponse, HttpError> {
            match self.0 {
                "timeout" => Err(HttpError::TimedOut),
                "hang" => {
                    let deadline = request.timeout.map(|timeout| Instant::now() + timeout);
                    loop {
                        if cancel.is_cancelled() {
                            return Err(HttpError::Cancelled);
                        }
                        if deadline.is_some_and(|deadline| Instant::now() >= deadline) {
                            return Err(HttpError::TimedOut);
                        }
                        std::thread::sleep(Duration::from_millis(5));
                    }
                }
                _ => Err(HttpError::Failed("reset".into())),
            }
        }
    }

    fn source(failure: &'static str) -> ReleaseManifestClient {
        ReleaseManifestClient::for_transport_test(
            Arc::new(Failing(failure)),
            WebUrl::parse("https://example.test/latest/manifest.json").unwrap(),
            false,
        )
        .unwrap()
    }

    // OpenAC AFingerprintThatCannotBeFetchedIsTreatedAsMissing.
    #[test]
    fn a_fingerprint_that_cannot_be_fetched_is_treated_as_missing() {
        for failure in ["timeout", "hang", "reset"] {
            let source = source(failure).with_fingerprint_timeout(Duration::from_millis(100));
            assert!(
                source
                    .fetch_launcher_fingerprint(&none())
                    .unwrap()
                    .is_none(),
                "{failure}"
            );
        }
    }

    // OpenAC AskingToStopStillStops.
    #[test]
    fn asking_to_stop_still_stops() {
        let source = source("hang");
        let stop = CancelToken::new();
        let stopper = stop.clone();
        let timer = std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(50));
            stopper.cancel();
        });
        assert!(matches!(
            source.fetch_launcher_fingerprint(&stop),
            Err(UpdateError::Cancelled)
        ));
        timer.join().unwrap();
    }

    fn configure(server: &FakeServer, fingerprint: Option<&str>) -> String {
        let rid = rid();
        let client = client_zip(&rid, "release-2");
        let launcher = launcher_zip(&rid, "release-2");
        configure_release(server, "2.0.0", "2.0.0", &rid, &client, &launcher);
        if let Some(fingerprint) = fingerprint {
            server.add(
                &FakeServer::url(LAUNCHER_FINGERPRINT_FILE_NAME),
                format!(r#"{{"schemaVersion":1,"version":"2.0.0","fingerprint":"{fingerprint}"}}"#)
                    .as_bytes(),
            );
        }
        rid
    }

    fn updater(root: &TempDir, server: &FakeServer, rid: &str) -> LauncherUpdater {
        LauncherUpdater::new(
            Arc::new(loopback_client(server)),
            server.transport(),
            Arc::new(ClientVersionStore::new(&root.path_set())),
            version("1.0.0"),
            rid,
            || false,
        )
        .unwrap()
        .with_launcher_fingerprint(Some(&same()))
    }

    // OpenAC AMatchingLauncherStaysAndInstallsTheNewClient.
    #[test]
    fn a_matching_launcher_stays_and_installs_the_new_client() {
        let root = TempDir::new("fingerprint-same");
        let server = FakeServer::new();
        let rid = configure(&server, Some(&same()));
        let updater = updater(&root, &server, &rid);
        updater.initialize(&none()).unwrap();
        let check = updater.check(&none()).unwrap();
        assert!(!check.is_launcher_update_available);
        assert!(check.is_launcher_minimum_satisfied);
        assert!(check.is_client_update_available);
        let installed = updater
            .install_client(&check, &mut |_| {}, &none())
            .unwrap();
        assert_eq!(installed.version, Some(version("2.0.0")));
    }

    // OpenAC ADifferentLauncherUpdatesFirst.
    #[test]
    fn a_different_launcher_updates_first() {
        let root = TempDir::new("fingerprint-other");
        let server = FakeServer::new();
        let rid = configure(&server, Some(&other()));
        let updater = updater(&root, &server, &rid);
        updater.initialize(&none()).unwrap();
        let check = updater.check(&none()).unwrap();
        assert!(check.is_launcher_update_available);
        assert!(!check.is_launcher_minimum_satisfied);
    }

    // OpenAC AReleaseWithoutAFingerprintUpdatesByVersion.
    #[test]
    fn a_release_without_a_fingerprint_updates_by_version() {
        let root = TempDir::new("fingerprint-none");
        let server = FakeServer::new();
        let rid = configure(&server, None);
        let updater = updater(&root, &server, &rid);
        updater.initialize(&none()).unwrap();
        let check = updater.check(&none()).unwrap();
        assert!(check.is_launcher_update_available);
        assert!(!check.is_launcher_minimum_satisfied);
    }
}
