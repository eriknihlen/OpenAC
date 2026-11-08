//! `plugincheck`. Ported from OpenAC
//! `tests/AcDream.PluginCheck.Tests/PluginCheckRunnerTests.cs` (golden
//! check names and messages), with the WebAssembly plugin manifest; the module checks and `pack` are this tool's own.

use std::collections::HashMap;
use std::io::Cursor;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::net::{HttpError, HttpRequest, HttpResponse, HttpTransport};
use ac_launcher_core::plugins::{
    InstalledPluginRecordStore, PLUGIN_INDEX_ASSET, PLUGIN_INDEX_TAG, PluginIndex, PluginInstaller,
    PluginReleaseChannel, PluginReleaseClient, PluginReleaseResolver, github,
};
use plugincheck::{
    CheckMode, CheckReport, CheckStatus, CheckVerdict, pack, pack_into_index, run, run_check,
};

#[path = "../../ac-launcher-core/tests/common/png.rs"]
mod png;

const ID: &str = "acme.hello";
const ENTRY: &str = "acme.hello.wasm";

struct Root(PathBuf);

impl Root {
    fn new(tag: &str) -> Self {
        let path = std::env::temp_dir()
            .join("openac-plugincheck-tests")
            .join(format!("{tag}-{}", ac_launcher_core::ids::unique_hex32()));
        std::fs::create_dir_all(&path).unwrap();
        Self(path)
    }

    fn join(&self, relative: &str) -> PathBuf {
        self.0.join(relative)
    }
}

impl Drop for Root {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// The `hello` test plugin's module (it imports the chat functions).
fn hello_wasm() -> Vec<u8> {
    std::fs::read(ac_plugin::fixtures::wasm_directory().join("hello.wasm")).unwrap()
}

fn manifest_json(id: &str, version: &str, permissions: bool) -> String {
    let permissions = if permissions {
        r#",
  "permissionsVersion": 1,
  "permissions": [{ "name": "chat", "note": "Answers /hello." }]"#
    } else {
        ""
    };
    format!(
        r#"{{
  "id": "{id}",
  "displayName": "Hello",
  "version": "{version}",
  "entry": "{ENTRY}",
  "apiVersion": "1.10",
  "minHostVersion": "0.1.0"{permissions}
}}"#
    )
}

fn valid_manifest() -> String {
    manifest_json(ID, "1.0.0", true)
}

fn write_folder(root: &Root, name: &str, manifest: &str, entry: &[u8]) -> PathBuf {
    let directory = root.join(name);
    std::fs::create_dir_all(&directory).unwrap();
    std::fs::write(directory.join("plugin.json"), manifest).unwrap();
    std::fs::write(directory.join(ENTRY), entry).unwrap();
    directory
}

fn write_valid_folder(root: &Root, name: &str) -> PathBuf {
    write_folder(root, name, &valid_manifest(), &hello_wasm())
}

fn zip_of(entries: &[(&str, &[u8])]) -> Vec<u8> {
    // The tool's own writer is private; the launcher tests' writer is the
    // reference archive shape. Stored entries, no attributes.
    let mut out = Vec::new();
    let mut central = Vec::new();
    for (name, content) in entries {
        let crc = ac_launcher_core::updates::zip::crc32(content);
        let offset = out.len() as u32;
        out.extend_from_slice(&0x0403_4b50u32.to_le_bytes());
        out.extend_from_slice(&20u16.to_le_bytes());
        out.extend_from_slice(&0x0800u16.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(&0x21u16.to_le_bytes());
        out.extend_from_slice(&crc.to_le_bytes());
        out.extend_from_slice(&(content.len() as u32).to_le_bytes());
        out.extend_from_slice(&(content.len() as u32).to_le_bytes());
        out.extend_from_slice(&(name.len() as u16).to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(name.as_bytes());
        out.extend_from_slice(content);
        central.extend_from_slice(&0x0201_4b50u32.to_le_bytes());
        central.extend_from_slice(&0x0014u16.to_le_bytes());
        central.extend_from_slice(&20u16.to_le_bytes());
        central.extend_from_slice(&0x0800u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0x21u16.to_le_bytes());
        central.extend_from_slice(&crc.to_le_bytes());
        central.extend_from_slice(&(content.len() as u32).to_le_bytes());
        central.extend_from_slice(&(content.len() as u32).to_le_bytes());
        central.extend_from_slice(&(name.len() as u16).to_le_bytes());
        for _ in 0..4 {
            central.extend_from_slice(&0u16.to_le_bytes());
        }
        central.extend_from_slice(&0u32.to_le_bytes());
        central.extend_from_slice(&offset.to_le_bytes());
        central.extend_from_slice(name.as_bytes());
    }
    let central_offset = out.len() as u32;
    out.extend_from_slice(&central);
    out.extend_from_slice(&0x0605_4b50u32.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out.extend_from_slice(&(entries.len() as u16).to_le_bytes());
    out.extend_from_slice(&(entries.len() as u16).to_le_bytes());
    out.extend_from_slice(&(central.len() as u32).to_le_bytes());
    out.extend_from_slice(&central_offset.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out
}

fn write_zip(root: &Root, name: &str, entries: &[(&str, &[u8])]) -> PathBuf {
    let path = root.join(name);
    std::fs::write(&path, zip_of(entries)).unwrap();
    path
}

fn write_valid_zip(root: &Root, name: &str, icon: Option<&[u8]>) -> PathBuf {
    let manifest = valid_manifest();
    let wasm = hello_wasm();
    let mut entries: Vec<(&str, &[u8])> =
        vec![("plugin.json", manifest.as_bytes()), (ENTRY, &wasm)];
    if let Some(icon) = icon {
        entries.push(("icon.png", icon));
    }
    write_zip(root, name, &entries)
}

fn check(report: &CheckReport, name: &str) -> (CheckStatus, String) {
    let item = report
        .item(name)
        .unwrap_or_else(|| panic!("no check '{name}' in {report:#?}"));
    (item.status, item.message.clone())
}

fn assert_no_failure(report: &CheckReport) {
    assert_eq!(report.verdict, CheckVerdict::WouldInstall, "{report:#?}");
    assert!(
        report
            .checks
            .iter()
            .all(|item| item.status != CheckStatus::Fail),
        "{report:#?}"
    );
}

// OpenAC PluginCheckRunnerTests.AValidDirectoryPasses.
#[test]
fn a_valid_directory_passes() {
    let root = Root::new("valid-directory");
    let report = run_check(&write_valid_folder(&root, "valid-directory")).unwrap();
    assert_eq!(report.mode, CheckMode::Directory);
    assert_no_failure(&report);
    let (status, message) = check(&report, "module matches its manifest");
    assert_eq!(status, CheckStatus::Pass);
    assert!(message.starts_with("API 1.10;"), "{message}");
    assert!(message.contains("Permissions used: chat"), "{message}");
}

// OpenAC PluginCheckRunnerTests.AValidZipPasses.
#[test]
fn a_valid_zip_passes() {
    let root = Root::new("valid-zip");
    let report = run_check(&write_valid_zip(&root, "valid-zip.zip", None)).unwrap();
    assert_eq!(report.mode, CheckMode::Zip);
    assert_no_failure(&report);
    // A local file may be named anything; the release name is only noted.
    assert_eq!(check(&report, "release asset name").0, CheckStatus::Skip);
}

// OpenAC PluginCheckRunnerTests.BadManifestJsonFails.
#[test]
fn bad_manifest_json_fails() {
    let root = Root::new("bad-json");
    let directory = write_folder(&root, "bad-json", "{ not json", &[1]);
    let report = run_check(&directory).unwrap();
    assert_eq!(report.verdict, CheckVerdict::WouldBeRefused);
    let (status, message) = check(&report, "plugin.json parses");
    assert_eq!(status, CheckStatus::Fail);
    assert!(message.to_lowercase().contains("invalid json"), "{message}");
}

#[test]
fn an_openac_csharp_plugin_fails_as_built_for_openac() {
    let root = Root::new("csharp");
    let directory = write_folder(
        &root,
        "csharp",
        r#"{ "id": "acme.hello", "displayName": "Hello", "version": "1.0.0",
            "entryDll": "acme.hello.dll", "apiVersion": 1, "minHostVersion": "0.1.0",
            "hosts": ["headless"] }"#,
        &[1],
    );
    let report = run_check(&directory).unwrap();
    let (status, message) = check(&report, "plugin.json parses");
    assert_eq!(status, CheckStatus::Fail);
    assert!(message.starts_with("built for OpenAC (C#)"), "{message}");
}

// OpenAC PluginCheckRunnerTests.AnInvalidIdFails.
#[test]
fn an_invalid_id_fails() {
    let root = Root::new("bad-id");
    let directory = write_folder(
        &root,
        "bad-id",
        &manifest_json("Acme.Hello", "1.0.0", true),
        &hello_wasm(),
    );
    let report = run_check(&directory).unwrap();
    assert_eq!(report.verdict, CheckVerdict::WouldBeRefused);
    let (status, message) = check(&report, "manifest satisfies install rules");
    assert_eq!(status, CheckStatus::Fail);
    assert!(message.contains("namespaced pattern"), "{message}");
}

// OpenAC PluginCheckRunnerTests.AnInvalidVersionFails.
#[test]
fn an_invalid_version_fails() {
    let root = Root::new("bad-version");
    let directory = write_folder(
        &root,
        "bad-version",
        &manifest_json(ID, "not-a-version", true),
        &hello_wasm(),
    );
    let report = run_check(&directory).unwrap();
    assert_eq!(report.verdict, CheckVerdict::WouldBeRefused);
    let (status, message) = check(&report, "manifest satisfies install rules");
    assert_eq!(status, CheckStatus::Fail);
    assert!(message.contains("SemVer"), "{message}");
}

// OpenAC PluginCheckRunnerTests.AnOversizedIconFailsInDirectoryMode.
#[test]
fn an_oversized_icon_fails_in_directory_mode() {
    let root = Root::new("oversized-icon");
    let directory = write_valid_folder(&root, "oversized-icon");
    std::fs::write(directory.join("icon.png"), png::over_maximum_bytes()).unwrap();
    let report = run_check(&directory).unwrap();
    assert_eq!(report.verdict, CheckVerdict::WouldBeRefused);
    let (status, message) = check(&report, "folder passes the direct-install checks");
    assert_eq!(status, CheckStatus::Fail);
    assert!(message.contains("larger than"), "{message}");
}

// OpenAC PluginCheckRunnerTests.AWrongDimensionIconFailsInZipMode.
#[test]
fn a_wrong_dimension_icon_fails_in_zip_mode() {
    let root = Root::new("wrong-dimensions");
    let zip = write_valid_zip(
        &root,
        "wrong-dimensions.zip",
        Some(&png::wrong_dimensions()),
    );
    let report = run_check(&zip).unwrap();
    assert_eq!(report.verdict, CheckVerdict::WouldBeRefused);
    let (status, message) = check(&report, "plugin icon");
    assert_eq!(status, CheckStatus::Fail);
    assert!(message.contains("64x64"), "{message}");
}

// OpenAC PluginCheckRunnerTests.AJpegIconAtRootFailsInZipMode: a JPEG is not
// an allowed file type at all now.
#[test]
fn a_jpeg_icon_at_root_fails_in_zip_mode() {
    let root = Root::new("jpeg-icon");
    let manifest = valid_manifest();
    let wasm = hello_wasm();
    let zip = write_zip(
        &root,
        "jpeg-icon.zip",
        &[
            ("plugin.json", manifest.as_bytes()),
            (ENTRY, &wasm),
            ("icon.jpg", &[1, 2, 3]),
        ],
    );
    let report = run_check(&zip).unwrap();
    assert_eq!(report.verdict, CheckVerdict::WouldBeRefused);
    let (status, message) = check(&report, "plugin content policy");
    assert_eq!(status, CheckStatus::Fail);
    assert!(message.contains("icon.jpg"), "{message}");
}

#[test]
fn a_dotnet_assembly_in_the_zip_fails_the_content_policy() {
    let root = Root::new("dll");
    let manifest = valid_manifest();
    let wasm = hello_wasm();
    let zip = write_zip(
        &root,
        "dll.zip",
        &[
            ("plugin.json", manifest.as_bytes()),
            (ENTRY, &wasm),
            ("Helper.dll", &[1, 2, 3]),
        ],
    );
    let (status, message) = check(&run_check(&zip).unwrap(), "plugin content policy");
    assert_eq!(status, CheckStatus::Fail);
    assert!(message.contains("disallowed extension"), "{message}");
}

// OpenAC PluginCheckRunnerTests.AnUnsafeZipEntryFails.
#[test]
fn an_unsafe_zip_entry_fails() {
    let root = Root::new("path-traversal");
    let manifest = valid_manifest();
    let zip = write_zip(
        &root,
        "path-traversal.zip",
        &[
            ("plugin.json", manifest.as_bytes()),
            (ENTRY, &[1, 2, 3]),
            ("../escaped.txt", &[1]),
        ],
    );
    let report = run_check(&zip).unwrap();
    assert_eq!(report.verdict, CheckVerdict::WouldBeRefused);
    assert_eq!(check(&report, "zip extracts safely").0, CheckStatus::Fail);
}

// OpenAC PluginCheckRunnerTests.AMissingPathThrowsUsage.
#[test]
fn a_missing_path_is_a_usage_error() {
    let root = Root::new("missing");
    assert!(run_check(&root.join("does-not-exist")).is_err());
}

// OpenAC PluginCheckRunnerTests.ANonZipFileThrowsUsage.
#[test]
fn a_non_zip_file_is_a_usage_error() {
    let root = Root::new("not-zip");
    let text = root.join("notes.txt");
    std::fs::write(&text, "not a plugin").unwrap();
    assert!(run_check(&text).is_err());
}

fn run_with(arguments: &[&str]) -> (i32, String, String) {
    let arguments: Vec<String> = arguments.iter().map(|a| (*a).to_owned()).collect();
    let mut output = Vec::new();
    let mut error = Vec::new();
    let code = run(&arguments, &mut output, &mut error);
    (
        code,
        String::from_utf8(output).unwrap(),
        String::from_utf8(error).unwrap(),
    )
}

// OpenAC PluginCheckRunnerTests.EntryPointExitCodesMatchTheVerdict.
#[test]
fn entry_point_exit_codes_match_the_verdict() {
    let root = Root::new("exit-codes");
    let valid = write_valid_folder(&root, "exit-code-valid");
    let refused = write_folder(
        &root,
        "exit-code-refused",
        &manifest_json("Bad.Id", "1.0.0", true),
        &hello_wasm(),
    );
    let missing = root.join("exit-code-missing");
    assert_eq!(run_with(&[valid.to_str().unwrap()]).0, 0);
    assert_eq!(run_with(&[refused.to_str().unwrap()]).0, 1);
    assert_eq!(run_with(&[missing.to_str().unwrap()]).0, 2);
    assert_eq!(run_with(&[]).0, 2);
    assert_eq!(run_with(&["a", "b"]).0, 2);
}

// OpenAC PluginCheckRunnerTests.JsonModePutsOnlyJsonOnStdout.
#[test]
fn json_mode_puts_only_json_on_stdout() {
    let root = Root::new("json-output");
    let directory = write_valid_folder(&root, "json-output");
    let (code, output, error) = run_with(&[directory.to_str().unwrap(), "--json"]);
    assert_eq!(code, 0);
    assert_eq!(error, "");
    let document = ac_host::json::parse(output.as_bytes()).unwrap();
    let mut object = ac_host::json::ObjectReader::new(&document).unwrap();
    let text = |node: Option<&ac_host::json::Node>| {
        ac_host::json::string_or_null(node.unwrap())
            .unwrap()
            .unwrap()
    };
    assert_eq!(
        ac_host::json::int32(object.take("schemaVersion").unwrap()).unwrap(),
        1
    );
    assert_eq!(text(object.take("mode")), "directory");
    assert_eq!(text(object.take("verdict")), "would-install");
    assert!(
        !ac_host::json::array_items(object.take("checks").unwrap())
            .unwrap()
            .is_empty()
    );
    // The document is the only thing written; the trailing newline is the
    // one character left over.
    assert!(output.ends_with('\n'));
    assert_eq!(output.trim_end().lines().count(), 1);
}

// --- The module checks (this tool's own) ---------------------------------------

#[test]
fn an_import_whose_permission_is_not_declared_fails() {
    let root = Root::new("undeclared");
    let directory = write_folder(
        &root,
        "undeclared",
        &manifest_json(ID, "1.0.0", false),
        &hello_wasm(),
    );
    let report = run_check(&directory).unwrap();
    assert_eq!(report.verdict, CheckVerdict::WouldBeRefused);
    let (status, message) = check(&report, "module matches its manifest");
    assert_eq!(status, CheckStatus::Fail);
    assert!(
        message.contains("needs the 'chat' permission plugin.json does not declare"),
        "{message}"
    );
}

#[test]
fn an_entry_that_is_not_webassembly_fails() {
    let root = Root::new("not-wasm");
    let directory = write_folder(&root, "not-wasm", &valid_manifest(), &[1, 2, 3]);
    let (status, message) = check(
        &run_check(&directory).unwrap(),
        "module matches its manifest",
    );
    assert_eq!(status, CheckStatus::Fail);
    assert!(
        message.contains("not a valid WebAssembly module"),
        "{message}"
    );
}

/// A module with nothing but an `openac.api` custom section.
fn module_with_api_section(version: &str) -> Vec<u8> {
    let name = b"openac.api";
    let mut section = vec![name.len() as u8];
    section.extend_from_slice(name);
    section.extend_from_slice(version.as_bytes());
    let mut module = b"\0asm\x01\0\0\0".to_vec();
    module.push(0);
    module.push(section.len() as u8);
    module.extend(section);
    module
}

#[test]
fn a_module_built_for_another_api_version_than_its_manifest_fails() {
    let root = Root::new("api-mismatch");
    let directory = write_folder(
        &root,
        "api-mismatch",
        &valid_manifest(),
        &module_with_api_section("2.0"),
    );
    let (status, message) = check(
        &run_check(&directory).unwrap(),
        "module matches its manifest",
    );
    assert_eq!(status, CheckStatus::Fail);
    assert!(
        message.starts_with("the .wasm was built for API 2.0 but plugin.json says 1.10"),
        "{message}"
    );
}

#[test]
fn a_module_without_the_sdk_api_section_fails() {
    let root = Root::new("no-section");
    let directory = write_folder(&root, "no-section", &valid_manifest(), b"\0asm\x01\0\0\0");
    let (status, message) = check(
        &run_check(&directory).unwrap(),
        "module matches its manifest",
    );
    assert_eq!(status, CheckStatus::Fail);
    assert!(message.contains("openac.api section"), "{message}");
}

/// The `hello` module as another SDK would have built it: its layout
/// fingerprint replaced, or left out.
fn hello_wasm_with_layout(layout: Option<&[u8]>) -> Vec<u8> {
    ac_plugin::fixtures::with_custom_section(&hello_wasm(), ac_plugin::abi::LAYOUT_SECTION, layout)
}

#[test]
fn a_module_built_against_another_record_layout_fails() {
    // Every import's request or reply shaped otherwise (as `loot_corpses`
    // was for the MossTank build that panicked on its first corpse list).
    let mut layout = ac_plugin::abi::layout::LAYOUT_SECTION_BYTES.to_vec();
    for shape in (16..layout.len()).step_by(16) {
        layout[shape] ^= 1;
    }
    let root = Root::new("layout-other");
    let directory = write_folder(
        &root,
        "layout-other",
        &valid_manifest(),
        &hello_wasm_with_layout(Some(&layout)),
    );
    let report = run_check(&directory).unwrap();
    assert_eq!(report.verdict, CheckVerdict::WouldBeRefused);
    let (status, message) = check(&report, "module matches its manifest");
    assert_eq!(status, CheckStatus::Fail);
    assert!(
        message.starts_with(
            "the .wasm was built against a different plugin API layout (the request or reply of \
             openac."
        ),
        "{message}"
    );
    assert!(
        message.contains("differs from this client's); rebuild it against this client's SDK."),
        "{message}"
    );
    assert!(
        message.contains("Rebuild the plugin against the openac-plugin SDK"),
        "{message}"
    );
}

#[test]
fn a_module_without_a_layout_fingerprint_fails() {
    // An SDK from before the fingerprint (the MossTank build that panicked
    // on its first corpse list passed plugincheck).
    let root = Root::new("layout-none");
    let directory = write_folder(
        &root,
        "layout-none",
        &valid_manifest(),
        &hello_wasm_with_layout(None),
    );
    let report = run_check(&directory).unwrap();
    assert_eq!(report.verdict, CheckVerdict::WouldBeRefused);
    let (status, message) = check(&report, "module matches its manifest");
    assert_eq!(status, CheckStatus::Fail);
    assert!(
        message.starts_with(
            "the .wasm was built against a different plugin API layout (it has no \
             openac.layout section"
        ),
        "{message}"
    );
}

#[test]
fn a_declared_but_unused_permission_is_noted_not_refused() {
    let root = Root::new("unused");
    let manifest = manifest_json(ID, "1.0.0", true).replace(
        r#"[{ "name": "chat", "note": "Answers /hello." }]"#,
        r#"[{ "name": "chat", "note": "Answers /hello." }, { "name": "clipboard", "note": "Copies." }]"#,
    );
    let directory = write_folder(&root, "unused", &manifest, &hello_wasm());
    let report = run_check(&directory).unwrap();
    assert_no_failure(&report);
    let (_, message) = check(&report, "module matches its manifest");
    assert!(
        message.contains("Declared but not used: clipboard"),
        "{message}"
    );
}

#[test]
fn a_permission_checked_per_call_is_never_called_unused() {
    // The profiles folder is asked for by name in a storage call, so no
    // import shows that a plugin uses it.
    let root = Root::new("per-call");
    let manifest = manifest_json(ID, "1.0.0", true).replace(
        r#"[{ "name": "chat", "note": "Answers /hello." }]"#,
        r#"[{ "name": "chat", "note": "Answers /hello." }, { "name": "profiles", "note": "Reads your profiles." }]"#,
    );
    let directory = write_folder(&root, "per-call", &manifest, &hello_wasm());
    let report = run_check(&directory).unwrap();
    assert_no_failure(&report);
    let (_, message) = check(&report, "module matches its manifest");
    assert!(message.contains("declared: chat, profiles"), "{message}");
    assert!(!message.contains("Declared but not used"), "{message}");
}

// --- pack ---------------------------------------------------------------------------

fn hello_folder(root: &Root) -> PathBuf {
    let folder = write_valid_folder(root, "hello-src");
    std::fs::write(folder.join("icon.png"), png::valid()).unwrap();
    std::fs::create_dir_all(folder.join("markup")).unwrap();
    std::fs::write(folder.join("markup").join("panel.xml"), "<panel/>").unwrap();
    std::fs::write(folder.join(".DS_Store"), [0]).unwrap();
    folder
}

#[test]
fn pack_writes_the_release_assets_and_the_zip_passes() {
    let root = Root::new("pack");
    let folder = hello_folder(&root);
    let out = root.join("release");
    let packed = pack(&folder, &out).unwrap();
    assert_eq!(packed.tag, "wasm-v1.0.0");
    assert_eq!(packed.zip, out.join("acme.hello-1.0.0.zip"));
    for name in [
        "plugin.json",
        "acme.hello-1.0.0.zip",
        "acme.hello-1.0.0.zip.sha256",
        "icon.png",
    ] {
        assert!(out.join(name).is_file(), "{name}");
    }
    let report = run_check(&packed.zip).unwrap();
    assert_no_failure(&report);
    for name in ["sha256 sidecar", "release asset name", "plugin icon"] {
        assert_eq!(check(&report, name).0, CheckStatus::Pass, "{name}");
    }
    // The same folder packs to the same bytes.
    let first = std::fs::read(&packed.zip).unwrap();
    pack(&folder, &root.join("again")).unwrap();
    assert_eq!(
        std::fs::read(root.join("again").join("acme.hello-1.0.0.zip")).unwrap(),
        first
    );
    // The command reports the check of what it wrote.
    let (code, output, _) = run_with(&[
        "pack",
        folder.to_str().unwrap(),
        root.join("third").to_str().unwrap(),
    ]);
    assert_eq!(code, 0, "{output}");
    assert!(output.contains("wasm-v1.0.0"), "{output}");
    assert!(
        output.contains("Verdict: this plugin would install."),
        "{output}"
    );
}

#[test]
fn pack_refuses_a_manifest_the_launcher_would_refuse() {
    let root = Root::new("pack-refused");
    let folder = write_folder(
        &root,
        "bad",
        &manifest_json("Bad.Id", "1.0.0", true),
        &hello_wasm(),
    );
    let (code, _, error) = run_with(&[
        "pack",
        folder.to_str().unwrap(),
        root.join("out").to_str().unwrap(),
    ]);
    assert_eq!(code, 1);
    assert!(error.contains("namespaced pattern"), "{error}");
    assert!(!root.join("out").exists());
}

// A repository publishing several plugins: each is packed under its own
// tag, and its entry merged into the repository's index.
#[test]
fn pack_with_an_index_tags_by_plugin_id_and_merges_its_entry() {
    let root = Root::new("pack-index");
    let folder = hello_folder(&root);
    let index = root.join("plugins-index.json");
    std::fs::write(
        &index,
        r#"{ "schemaVersion": 1, "plugins": [
            { "id": "acme.other", "stable": "wasm-acme.other-v2.0.0" },
            { "id": "acme.hello", "stable": "wasm-acme.hello-v0.9.0",
              "beta": "wasm-acme.hello-v1.0.0-beta.1" } ] }"#,
    )
    .unwrap();
    let packed = pack_into_index(&folder, &root.join("release"), &index).unwrap();
    assert_eq!(packed.tag, "wasm-acme.hello-v1.0.0");
    let merged = PluginIndex::parse(&std::fs::read_to_string(&index).unwrap()).unwrap();
    let hello = merged.entry(ID).unwrap();
    assert_eq!(hello.stable.as_deref(), Some("wasm-acme.hello-v1.0.0"));
    assert_eq!(hello.beta.as_deref(), Some("wasm-acme.hello-v1.0.0-beta.1"));
    assert_eq!(
        merged.entry("acme.other").unwrap().stable.as_deref(),
        Some("wasm-acme.other-v2.0.0")
    );

    // A new index file is started; the command says where each part goes.
    let fresh = root.join("new-index.json");
    let (code, output, error) = run_with(&[
        "pack",
        folder.to_str().unwrap(),
        root.join("again").to_str().unwrap(),
        "--index",
        fresh.to_str().unwrap(),
    ]);
    assert_eq!(code, 0, "{output}{error}");
    assert!(
        output.contains("the release tagged wasm-acme.hello-v1.0.0"),
        "{output}"
    );
    assert!(
        output.contains(&format!(
            "as {PLUGIN_INDEX_ASSET} to the release tagged {PLUGIN_INDEX_TAG}"
        )),
        "{output}"
    );
    let started = PluginIndex::parse(&std::fs::read_to_string(&fresh).unwrap()).unwrap();
    assert_eq!(started.plugins.len(), 1);
}

#[test]
fn pack_refuses_a_malformed_index_and_writes_nothing() {
    let root = Root::new("pack-bad-index");
    let folder = hello_folder(&root);
    let index = root.join("plugins-index.json");
    std::fs::write(
        &index,
        r#"{ "schemaVersion": 1, "plugins": [ { "id": "x" } ] }"#,
    )
    .unwrap();
    let (code, _, error) = run_with(&[
        "pack",
        folder.to_str().unwrap(),
        root.join("out").to_str().unwrap(),
        "--index",
        index.to_str().unwrap(),
    ]);
    assert_eq!(code, 1);
    assert!(error.contains("plugin index is invalid"), "{error}");
    assert!(!root.join("out").exists());
    // Only `--index <file>` follows the two paths.
    let out = root.join("out");
    for extra in [vec!["--index"], vec!["--other", "x"]] {
        let mut arguments = vec!["pack", folder.to_str().unwrap(), out.to_str().unwrap()];
        arguments.extend(extra);
        assert_eq!(run_with(&arguments).0, 2);
    }
}

// A hotfix of an older line (1.0.0 packed after 1.2.0) is packed, but the
// index keeps naming the higher version, and the command says so.
#[test]
fn pack_never_moves_the_index_back_to_a_lower_version() {
    let root = Root::new("pack-index-hotfix");
    let folder = hello_folder(&root);
    let index = root.join("plugins-index.json");
    let previous = r#"{ "schemaVersion": 1, "plugins": [
        { "id": "acme.hello", "stable": "wasm-acme.hello-v1.2.0" } ] }"#;
    std::fs::write(&index, previous).unwrap();
    let (code, output, error) = run_with(&[
        "pack",
        folder.to_str().unwrap(),
        root.join("release").to_str().unwrap(),
        "--index",
        index.to_str().unwrap(),
    ]);
    assert_eq!(code, 0, "{output}{error}");
    assert!(
        output.contains("the release tagged wasm-acme.hello-v1.0.0"),
        "{output}"
    );
    assert!(
        output.contains("still names wasm-acme.hello-v1.2.0, a higher version"),
        "{output}"
    );
    assert!(!output.contains("Then upload"), "{output}");
    assert_eq!(std::fs::read_to_string(&index).unwrap(), previous);
    assert!(root.join("release").join("acme.hello-1.0.0.zip").is_file());
}

// The index file is read strictly: bytes that are not UTF-8 are refused,
// not replaced.
#[test]
fn pack_refuses_an_index_that_is_not_utf8() {
    let root = Root::new("pack-index-utf8");
    let folder = hello_folder(&root);
    let index = root.join("plugins-index.json");
    std::fs::write(
        &index,
        b"{ \"schemaVersion\": 1, \"plugins\": [ { \"id\": \"acme.\xFF\", \"stable\": \"wasm-v1.0.0\" } ] }",
    )
    .unwrap();
    let (code, _, error) = run_with(&[
        "pack",
        folder.to_str().unwrap(),
        root.join("out").to_str().unwrap(),
        "--index",
        index.to_str().unwrap(),
    ]);
    assert_eq!(code, 1);
    assert!(error.contains("it is not UTF-8"), "{error}");
    assert!(!root.join("out").exists());
}

#[test]
fn a_failed_index_write_leaves_the_previous_index_whole() {
    let root = Root::new("pack-index-write");
    let folder = hello_folder(&root);
    let index = root.join("plugins-index.json");
    let previous = r#"{ "schemaVersion": 1, "plugins": [
        { "id": "acme.other", "stable": "wasm-acme.other-v2.0.0" } ] }"#;
    std::fs::write(&index, previous).unwrap();
    // Something in the way of the replacement's temporary file.
    let blocker = root.join("plugins-index.json.tmp");
    std::fs::create_dir_all(blocker.join("inside")).unwrap();
    assert!(pack_into_index(&folder, &root.join("release"), &index).is_err());
    assert_eq!(std::fs::read_to_string(&index).unwrap(), previous);
}

// --- A packed release installs through the launcher ------------------------------

/// GitHub as a map of addresses to files; anything else is 404.
#[derive(Default, Clone)]
struct Releases(Arc<Mutex<HashMap<String, Vec<u8>>>>);

impl Releases {
    fn add(&self, url: String, body: Vec<u8>) {
        self.0.lock().unwrap().insert(url, body);
    }
}

impl HttpTransport for Releases {
    fn get(&self, request: &HttpRequest, _: &CancelToken) -> Result<HttpResponse, HttpError> {
        let body = self
            .0
            .lock()
            .unwrap()
            .get(&request.url.to_string())
            .cloned();
        let status = if body.is_some() { 200 } else { 404 };
        let body = body.unwrap_or_default();
        Ok(HttpResponse {
            status,
            location: None,
            content_length: Some(body.len() as u64),
            content_encoding: Vec::new(),
            requested_url: request.url.clone(),
            body: Box::new(Cursor::new(body)),
        })
    }
}

#[test]
fn a_packed_release_installs_through_the_launcher_installer() {
    let root = Root::new("pack-install");
    let folder = hello_folder(&root);
    let out = root.join("release");
    let packed = pack(&folder, &out).unwrap();
    let repo = "acme/openac-hello";
    let releases = Releases::default();
    for file in &packed.files {
        let name = file.file_name().unwrap().to_string_lossy().into_owned();
        releases.add(
            github::tagged_asset(repo, &packed.tag, &name)
                .unwrap()
                .to_string(),
            std::fs::read(file).unwrap(),
        );
    }
    let paths =
        ac_host::PathSet::from_members(root.join("config"), root.join("data"), root.join("cache"));
    let records = Arc::new(Mutex::new(InstalledPluginRecordStore::for_paths(&paths)));
    let installer = PluginInstaller::new(&paths, Arc::new(releases), records);
    let result = installer
        .install_or_update(
            repo,
            &packed.tag,
            None,
            None,
            None,
            None,
            &CancelToken::none(),
        )
        .unwrap();
    assert_eq!(result.id, ID);
    let installed = paths.plugins_directory().join(ID);
    for name in ["plugin.json", ENTRY, "icon.png", "markup/panel.xml"] {
        assert!(installed.join(name).is_file(), "{name}");
    }
    assert!(!installed.join(".DS_Store").exists());
    // What the launcher installed is what a session loads.
    let report = run_check(&installed).unwrap();
    assert_no_failure(&report);
}

// A plugin pushed off its repository's feed is still found through the
// index pack wrote, and installs.
#[test]
fn a_release_packed_into_an_index_resolves_and_installs() {
    let root = Root::new("pack-index-install");
    let folder = hello_folder(&root);
    let index = root.join("plugins-index.json");
    let packed = pack_into_index(&folder, &root.join("release"), &index).unwrap();
    let repo = "acme/openac-suite";
    let releases = Releases::default();
    for file in &packed.files {
        let name = file.file_name().unwrap().to_string_lossy().into_owned();
        releases.add(
            github::tagged_asset(repo, &packed.tag, &name)
                .unwrap()
                .to_string(),
            std::fs::read(file).unwrap(),
        );
    }
    releases.add(
        github::tagged_asset(repo, PLUGIN_INDEX_TAG, PLUGIN_INDEX_ASSET)
            .unwrap()
            .to_string(),
        std::fs::read(&index).unwrap(),
    );
    // The feed shows only another plugin's releases.
    let entries: String = (0..10)
        .map(|patch| {
            format!(
                r#"<entry><link rel="alternate" href="https://github.com/{repo}/releases/tag/wasm-acme.other-v1.0.{patch}"/></entry>"#
            )
        })
        .collect();
    releases.add(
        github::releases_feed(repo).unwrap().to_string(),
        format!(r#"<feed xmlns="http://www.w3.org/2005/Atom">{entries}</feed>"#).into_bytes(),
    );
    let transport = Arc::new(releases);
    let resolver = PluginReleaseResolver::new(PluginReleaseClient::new(transport.clone()));
    let resolved = resolver
        .resolve_plugin(repo, ID, PluginReleaseChannel::Stable, &CancelToken::none())
        .unwrap();
    let tag = resolved.resolution().unwrap().tag.clone();
    assert_eq!(tag, packed.tag);
    let paths =
        ac_host::PathSet::from_members(root.join("config"), root.join("data"), root.join("cache"));
    let records = Arc::new(Mutex::new(InstalledPluginRecordStore::for_paths(&paths)));
    let installer = PluginInstaller::new(&paths, transport, records);
    let result = installer
        .install_or_update(repo, &tag, None, None, None, None, &CancelToken::none())
        .unwrap();
    assert_eq!(result.id, ID);
}
