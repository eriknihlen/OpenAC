//! A plugin's `plugin.json`.
//!
//! OpenAC's manifest with the changes of the WebAssembly API: `entry` names a `.wasm` file (a manifest naming an
//! `entryDll` was built for OpenAC's C# host and is refused), `apiVersion`
//! is `"MAJOR.MINOR"`, `capabilities` became enforced `permissions` (same
//! entry shape and note rules), and `limits.memoryMiB` caps the plugin's
//! memory. `kinds` and `hosts` are gone (unknown members are ignored, as
//! OpenAC ignores them). Member names match ignoring case, and a name
//! repeated anywhere (ignoring case) is refused.

use std::path::{Component, Path};

use ac_host_core::json::{self, JsonErrorKind, Member, Node, Value};
use ac_plugin_version::{ApiVersion, PERMISSIONS_VERSION, Permission};

use crate::version::HostVersion;

/// The memory cap a manifest gets when it names none, MiB.
pub const DEFAULT_MEMORY_MIB: u32 = 256;

/// The largest memory cap a manifest may ask for, MiB.
pub const MAX_MEMORY_MIB: u32 = 1024;

/// The longest permission note, in UTF-16 units (as OpenAC counts).
pub const MAX_NOTE_LEN: usize = 120;

/// One declared permission.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PermissionDeclaration {
    /// Which.
    pub permission: Permission,
    /// The author's one-sentence disclosure, trimmed.
    pub note: String,
    /// For `network`: the hosts it may reach without asking the player.
    pub hosts: Vec<String>,
}

/// A parsed `plugin.json`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginManifest {
    /// The plugin id (`owner.name`).
    pub id: String,
    /// The name shown to players.
    pub display_name: String,
    /// The plugin's own version.
    pub version: String,
    /// The `.wasm` file, relative to the plugin's folder.
    pub entry: String,
    /// The plugin API version it was built for.
    pub api_version: ApiVersion,
    /// Other plugins it names as dependencies (kept, not acted on, as
    /// OpenAC).
    pub dependencies: Vec<String>,
    /// The oldest client it supports.
    pub min_host_version: Option<HostVersion>,
    /// The newest client it supports.
    pub max_host_version: Option<HostVersion>,
    /// Client versions it is known broken on.
    pub skip_host_versions: Vec<HostVersion>,
    /// The permission vocabulary version (0 when it declares none).
    pub permissions_version: u32,
    /// What it is allowed to do beyond the basics, in declared order.
    pub permissions: Vec<PermissionDeclaration>,
    /// Its memory cap, MiB.
    pub memory_mib: u32,
}

/// Why a manifest is unusable; the text is what a player or author reads.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
#[error("{0}")]
pub struct ManifestError(pub String);

fn invalid(message: impl Into<String>) -> ManifestError {
    ManifestError(message.into())
}

impl PluginManifest {
    /// Whether it declares `permission`.
    #[must_use]
    pub fn allows(&self, permission: Permission) -> bool {
        self.permissions.iter().any(|p| p.permission == permission)
    }

    /// Reads a manifest.
    ///
    /// # Errors
    /// The first rule the text breaks.
    pub fn parse(text: &str) -> Result<Self, ManifestError> {
        let root = json::parse(text.as_bytes()).map_err(|error| {
            if error.kind == JsonErrorKind::DuplicateMember
                && let Some(name) = member_name_at(text, error.at.line, error.at.column)
            {
                return invalid(format!("duplicate property: {name}"));
            }
            invalid(format!("invalid json: {error}"))
        })?;
        reject_case_insensitive_duplicates(&root)?;
        if json::is_null(&root) {
            return Err(invalid("manifest is empty"));
        }
        let object = Object::new(&root)?;

        let id = require(object.text("id")?, "id")?;
        let display_name = require(object.text("displayName")?, "displayName")?;
        let version = require(object.text("version")?, "version")?;
        if object.get("entryDll").is_some() {
            return Err(invalid(
                "built for OpenAC (C#): entryDll names a .NET assembly; this client loads \
                 WebAssembly plugins named by entry",
            ));
        }
        let entry = require(object.text("entry")?, "entry")?;
        if !is_wasm_inside_folder(&entry) {
            return Err(invalid(format!(
                "entry must name a .wasm file inside the plugin folder: {entry}"
            )));
        }
        let api_version = match object.get("apiVersion").map(|n| &n.value) {
            None | Some(Value::Null) => return Err(invalid("missing required field: apiVersion")),
            Some(Value::String(text)) => ApiVersion::parse(text)
                .ok_or_else(|| invalid(format!("malformed apiVersion: {text}")))?,
            Some(_) => {
                return Err(invalid(
                    "apiVersion must be a string \"MAJOR.MINOR\" (a number is OpenAC's C# API)",
                ));
            }
        };
        let dependencies = object
            .string_list("dependencies")?
            .unwrap_or_default()
            .into_iter()
            .map(|d| d.unwrap_or_default())
            .collect();

        let min_host_version = host_version(object.text("minHostVersion")?, "minHostVersion")?;
        let max_host_version = host_version(object.text("maxHostVersion")?, "maxHostVersion")?;
        if let (Some(min), Some(max)) = (min_host_version, max_host_version)
            && min > max
        {
            return Err(invalid(format!(
                "minHostVersion ({min}) is greater than maxHostVersion ({max})"
            )));
        }
        let skip_host_versions = object
            .string_list("skipHostVersions")?
            .unwrap_or_default()
            .into_iter()
            .map(|value| {
                value
                    .as_deref()
                    .and_then(HostVersion::parse)
                    .ok_or_else(|| {
                        invalid(format!(
                            "malformed skipHostVersions entry: {}",
                            value.as_deref().unwrap_or("<null>")
                        ))
                    })
            })
            .collect::<Result<Vec<_>, _>>()?;

        let permissions_version = match object.get("permissionsVersion") {
            None => 0,
            Some(node) => json::uint32(node).map_err(|_| {
                invalid("invalid json: the 'permissionsVersion' member must be a whole number")
            })?,
        };
        let permissions = permissions(&object, permissions_version)?;
        let memory_mib = memory_limit(&object)?;

        Ok(Self {
            id,
            display_name,
            version,
            entry,
            api_version,
            dependencies,
            min_host_version,
            max_host_version,
            skip_host_versions,
            permissions_version,
            permissions,
            memory_mib,
        })
    }
}

/// An object whose members are looked up ignoring case.
struct Object<'a> {
    members: &'a [Member],
}

impl<'a> Object<'a> {
    fn new(node: &'a Node) -> Result<Self, ManifestError> {
        json::object_members(node)
            .map(|members| Self { members })
            .map_err(|error| invalid(format!("invalid json: {error}")))
    }

    fn get(&self, name: &str) -> Option<&'a Node> {
        self.members
            .iter()
            .find(|m| m.name.eq_ignore_ascii_case(name))
            .map(|m| &m.value)
    }

    fn text(&self, name: &str) -> Result<Option<String>, ManifestError> {
        match self.get(name).map(|n| &n.value) {
            None | Some(Value::Null) => Ok(None),
            Some(Value::String(text)) => Ok(Some(text.clone())),
            Some(_) => Err(invalid(format!(
                "invalid json: the '{name}' member must be a string"
            ))),
        }
    }

    fn string_list(&self, name: &str) -> Result<Option<Vec<Option<String>>>, ManifestError> {
        match self.get(name).map(|n| &n.value) {
            None | Some(Value::Null) => Ok(None),
            Some(Value::Array(items)) => items
                .iter()
                .map(|item| match &item.value {
                    Value::Null => Ok(None),
                    Value::String(text) => Ok(Some(text.clone())),
                    _ => Err(invalid(format!(
                        "invalid json: the '{name}' member must hold strings"
                    ))),
                })
                .collect::<Result<Vec<_>, _>>()
                .map(Some),
            Some(_) => Err(invalid(format!(
                "invalid json: the '{name}' member must be an array"
            ))),
        }
    }
}

fn require(value: Option<String>, field: &str) -> Result<String, ManifestError> {
    match value {
        Some(text) if !text.trim().is_empty() => Ok(text),
        _ => Err(invalid(format!("missing required field: {field}"))),
    }
}

fn host_version(value: Option<String>, field: &str) -> Result<Option<HostVersion>, ManifestError> {
    value
        .map(|text| {
            HostVersion::parse(&text).ok_or_else(|| invalid(format!("malformed {field}: {text}")))
        })
        .transpose()
}

/// A relative path to a `.wasm` file that stays inside the folder: no root,
/// no drive, no `..`.
fn is_wasm_inside_folder(entry: &str) -> bool {
    let path = Path::new(entry);
    let has_wasm_extension = path
        .extension()
        .and_then(|e| e.to_str())
        .is_some_and(|e| e.eq_ignore_ascii_case("wasm"));
    has_wasm_extension
        && !entry.contains(':')
        && !entry.starts_with(['/', '\\'])
        && path
            .components()
            .all(|c| matches!(c, Component::Normal(_) | Component::CurDir))
        && !entry.split(['/', '\\']).any(|part| part == "..")
}

fn permissions(
    object: &Object<'_>,
    version: u32,
) -> Result<Vec<PermissionDeclaration>, ManifestError> {
    let entries = match object.get("permissions").map(|n| &n.value) {
        None | Some(Value::Null) => return Ok(Vec::new()),
        Some(Value::Array(items)) => items,
        Some(_) => {
            return Err(invalid(
                "invalid json: the 'permissions' member must be an array",
            ));
        }
    };
    if entries.is_empty() {
        return Ok(Vec::new());
    }
    if version < 1 {
        return Err(invalid("permissions requires permissionsVersion >= 1"));
    }
    if version > PERMISSIONS_VERSION {
        return Err(invalid(format!(
            "permissionsVersion {version} is newer than this client's vocabulary \
             ({PERMISSIONS_VERSION})"
        )));
    }
    let mut declared: Vec<PermissionDeclaration> = Vec::new();
    for item in entries {
        if json::is_null(item) {
            return Err(invalid("permission entry is missing a name"));
        }
        let entry = Object::new(item)
            .map_err(|_| invalid("invalid json: a permission entry must be an object"))?;
        let name = match entry.text("name")? {
            Some(name) if !name.trim().is_empty() => name,
            _ => return Err(invalid("permission entry is missing a name")),
        };
        let permission = Permission::from_name(&name)
            .ok_or_else(|| invalid(format!("unknown permission: {name}")))?;
        if declared.iter().any(|d| d.permission == permission) {
            return Err(invalid(format!("duplicate permission: {name}")));
        }
        let note = note(entry.text("note")?.as_deref(), &name)?;
        let hosts = entry
            .string_list("hosts")?
            .unwrap_or_default()
            .into_iter()
            .map(|host| match host {
                Some(host) if !host.trim().is_empty() => Ok(host.trim().to_owned()),
                _ => Err(invalid(format!("permission '{name}' names an empty host"))),
            })
            .collect::<Result<Vec<_>, _>>()?;
        if !hosts.is_empty() && permission != Permission::Network {
            return Err(invalid(format!(
                "permission '{name}' takes no hosts (only network does)"
            )));
        }
        declared.push(PermissionDeclaration {
            permission,
            note,
            hosts,
        });
    }
    Ok(declared)
}

/// The note rules OpenAC applies to a capability's note: present, at most
/// [`MAX_NOTE_LEN`] UTF-16 units, no control or invisible formatting
/// characters, no links.
fn note(value: Option<&str>, name: &str) -> Result<String, ManifestError> {
    let trimmed = value.unwrap_or_default().trim();
    if trimmed.is_empty() {
        return Err(invalid(format!("permission '{name}' is missing a note")));
    }
    if trimmed.encode_utf16().count() > MAX_NOTE_LEN {
        return Err(invalid(format!(
            "permission '{name}' note exceeds {MAX_NOTE_LEN} characters"
        )));
    }
    for c in trimmed.chars() {
        if c.is_control() {
            return Err(invalid(format!(
                "permission '{name}' note contains a control character"
            )));
        }
        if is_format_character(c) {
            return Err(invalid(format!(
                "permission '{name}' note contains a disallowed formatting character"
            )));
        }
    }
    if trimmed.contains("://") {
        return Err(invalid(format!("permission '{name}' note contains a link")));
    }
    Ok(trimmed.to_owned())
}

/// Unicode format characters (category Cf): bidirectional controls and
/// zero-width characters that can disguise text.
fn is_format_character(c: char) -> bool {
    matches!(
        u32::from(c),
        0x00AD
            | 0x0600..=0x0605
            | 0x061C
            | 0x06DD
            | 0x070F
            | 0x0890..=0x0891
            | 0x08E2
            | 0x180E
            | 0x200B..=0x200F
            | 0x202A..=0x202E
            | 0x2060..=0x2064
            | 0x2066..=0x206F
            | 0xFEFF
            | 0xFFF9..=0xFFFB
            | 0x110BD
            | 0x110CD
            | 0x13430..=0x1343F
            | 0x1BCA0..=0x1BCA3
            | 0x1D173..=0x1D17A
            | 0xE0001
            | 0xE0020..=0xE007F
    )
}

fn memory_limit(object: &Object<'_>) -> Result<u32, ManifestError> {
    let limits = match object.get("limits") {
        None => return Ok(DEFAULT_MEMORY_MIB),
        Some(node) if json::is_null(node) => return Ok(DEFAULT_MEMORY_MIB),
        Some(node) => Object::new(node)
            .map_err(|_| invalid("invalid json: the 'limits' member must be an object"))?,
    };
    match limits.get("memoryMiB") {
        None => Ok(DEFAULT_MEMORY_MIB),
        Some(node) => match json::uint32(node) {
            Ok(mib) if (1..=MAX_MEMORY_MIB).contains(&mib) => Ok(mib),
            _ => Err(invalid(format!(
                "limits.memoryMiB must be a whole number from 1 to {MAX_MEMORY_MIB}"
            ))),
        },
    }
}

/// A member name repeated in one object, ignoring case: OpenAC's
/// "duplicate property: <name>", naming the later one.
fn reject_case_insensitive_duplicates(node: &Node) -> Result<(), ManifestError> {
    match &node.value {
        Value::Object(members) => {
            let mut seen: Vec<String> = Vec::with_capacity(members.len());
            for member in members {
                let folded = member.name.to_lowercase();
                if seen.contains(&folded) {
                    return Err(invalid(format!("duplicate property: {}", member.name)));
                }
                seen.push(folded);
                reject_case_insensitive_duplicates(&member.value)?;
            }
            Ok(())
        }
        Value::Array(items) => items
            .iter()
            .try_for_each(reject_case_insensitive_duplicates),
        _ => Ok(()),
    }
}

/// The member name whose opening quote is at 1-based `line` and `column`
/// (characters), unescaped.
fn member_name_at(text: &str, line: u32, column: u32) -> Option<String> {
    let line_text = text
        .lines()
        .nth(usize::try_from(line).ok()?.checked_sub(1)?)?;
    let start: usize = line_text
        .char_indices()
        .nth(usize::try_from(column).ok()?.checked_sub(1)?)?
        .0;
    let rest = &line_text[start..];
    let mut escaped = false;
    for (offset, c) in rest.char_indices().skip(1) {
        match c {
            '\\' if !escaped => escaped = true,
            '"' if !escaped => {
                let literal = &rest[..=offset];
                return match json::parse(literal.as_bytes()).ok()?.value {
                    Value::String(name) => Some(name),
                    _ => None,
                };
            }
            _ => escaped = false,
        }
    }
    None
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Core.Tests/Plugins/PluginManifestTests.cs`, with
    //! the WebAssembly changes.

    use super::*;

    fn parse(text: &str) -> Result<PluginManifest, String> {
        PluginManifest::parse(text).map_err(|e| e.0)
    }

    fn with(extra: &str) -> String {
        format!(
            r#"{{ "id": "x", "displayName": "X", "version": "1.0.0", "entry": "x.wasm", "apiVersion": "1.0"{extra} }}"#
        )
    }

    #[test]
    fn a_valid_manifest_parses() {
        let manifest = parse(
            r#"{
              "id": "edwards.tank",
              "displayName": "Tank",
              "version": "0.1.0",
              "entry": "tank.wasm",
              "apiVersion": "1.0"
            }"#,
        )
        .unwrap();
        assert_eq!(manifest.id, "edwards.tank");
        assert_eq!(manifest.display_name, "Tank");
        assert_eq!(manifest.version, "0.1.0");
        assert_eq!(manifest.entry, "tank.wasm");
        assert_eq!(manifest.api_version, ApiVersion::new(1, 0));
        assert!(manifest.dependencies.is_empty());
        assert!(manifest.permissions.is_empty());
        assert_eq!(manifest.memory_mib, DEFAULT_MEMORY_MIB);
    }

    #[test]
    fn a_missing_required_field_is_named() {
        assert_eq!(
            parse(r#"{ "id": "x", "version": "0.1.0", "entry": "x.wasm", "apiVersion": "1.0" }"#),
            Err("missing required field: displayName".into())
        );
        assert_eq!(
            parse(r#"{ "id": "x", "displayName": "X", "version": "0.1.0", "apiVersion": "1.0" }"#),
            Err("missing required field: entry".into())
        );
        assert_eq!(
            parse(r#"{ "id": "x", "displayName": "X", "version": "0.1.0", "entry": "x.wasm" }"#),
            Err("missing required field: apiVersion".into())
        );
    }

    #[test]
    fn malformed_json_is_refused() {
        assert!(parse("{ not json").unwrap_err().starts_with("invalid json"));
        assert_eq!(parse("null"), Err("manifest is empty".into()));
    }

    #[test]
    fn a_csharp_manifest_is_refused_as_built_for_openac() {
        let error = parse(
            r#"{ "id": "x", "displayName": "X", "version": "1.0.0", "entryDll": "x.dll", "apiVersion": 1 }"#,
        )
        .unwrap_err();
        assert!(error.starts_with("built for OpenAC (C#)"), "{error}");
    }

    #[test]
    fn the_entry_is_a_wasm_file_inside_the_folder() {
        for entry in ["sub/x.wasm", "X.WASM", "./x.wasm"] {
            let text = with("").replace("x.wasm", entry);
            assert_eq!(parse(&text).map(|m| m.entry), Ok(entry.to_owned()));
        }
        for entry in [
            "x.dll",
            "../x.wasm",
            "/x.wasm",
            "C:/x.wasm",
            "a/../../x.wasm",
            "x",
        ] {
            let text = with("").replace("x.wasm", entry);
            assert!(
                parse(&text)
                    .unwrap_err()
                    .starts_with("entry must name a .wasm file"),
                "{entry}"
            );
        }
    }

    #[test]
    fn the_api_version_is_major_dot_minor() {
        let text = with("").replace(r#""apiVersion": "1.0""#, r#""apiVersion": "1.0.0""#);
        assert_eq!(parse(&text), Err("malformed apiVersion: 1.0.0".into()));
        let text = with("").replace(r#""apiVersion": "1.0""#, r#""apiVersion": 1"#);
        assert!(
            parse(&text)
                .unwrap_err()
                .starts_with("apiVersion must be a string")
        );
    }

    #[test]
    fn host_fields_are_read() {
        let manifest = parse(&with(
            r#", "minHostVersion": "0.1.5", "maxHostVersion": "0.2.0", "skipHostVersions": ["0.1.7"]"#,
        ))
        .unwrap();
        assert_eq!(manifest.min_host_version, Some(HostVersion::new(0, 1, 5)));
        assert_eq!(manifest.max_host_version, Some(HostVersion::new(0, 2, 0)));
        assert_eq!(manifest.skip_host_versions, vec![HostVersion::new(0, 1, 7)]);
    }

    #[test]
    fn host_fields_absent_mean_any_version() {
        let manifest = parse(&with("")).unwrap();
        assert_eq!(manifest.min_host_version, None);
        assert_eq!(manifest.max_host_version, None);
        assert!(manifest.skip_host_versions.is_empty());
    }

    #[test]
    fn a_malformed_host_version_is_refused() {
        for version in ["v1.0.0", "1.0", "1.0.0-beta", "01.0.0"] {
            assert_eq!(
                parse(&with(&format!(r#", "minHostVersion": "{version}""#))),
                Err(format!("malformed minHostVersion: {version}"))
            );
        }
    }

    #[test]
    fn min_above_max_is_refused() {
        assert_eq!(
            parse(&with(
                r#", "minHostVersion": "0.2.0", "maxHostVersion": "0.1.5""#
            )),
            Err("minHostVersion (0.2.0) is greater than maxHostVersion (0.1.5)".into())
        );
    }

    #[test]
    fn a_duplicate_top_level_key_is_refused() {
        assert_eq!(
            parse(
                r#"{ "id": "x", "id": "y", "displayName": "X", "version": "1.0.0", "entry": "x.wasm", "apiVersion": "1.0" }"#
            ),
            Err("duplicate property: id".into())
        );
    }

    #[test]
    fn a_case_variant_duplicate_key_is_refused() {
        assert_eq!(
            parse(
                r#"{ "Id": "x", "id": "y", "displayName": "X", "version": "1.0.0", "entry": "x.wasm", "apiVersion": "1.0" }"#
            ),
            Err("duplicate property: id".into())
        );
    }

    #[test]
    fn a_duplicate_key_in_a_nested_object_is_refused() {
        assert_eq!(
            parse(&with(r#", "extra": { "name": "a", "name": "b" }"#)),
            Err("duplicate property: name".into())
        );
    }

    #[test]
    fn the_same_key_in_sibling_objects_is_allowed() {
        assert_eq!(
            parse(&with(r#", "a": { "name": "a" }, "b": { "name": "b" }"#)).map(|m| m.id),
            Ok("x".into())
        );
    }

    #[test]
    fn retired_members_are_ignored() {
        // kinds and hosts are gone with render packs and the two hosts.
        assert!(parse(&with(r#", "kinds": ["renderPack"], "hosts": ["headless"]"#)).is_ok());
    }

    #[test]
    fn permissions_are_read_with_their_notes() {
        let manifest = parse(&with(
            r#", "permissionsVersion": 1, "permissions": [
                { "name": "chat", "note": " Reads chat. " },
                { "name": "Network", "note": "Talks to its backend.", "hosts": ["example.org"] }
            ]"#,
        ))
        .unwrap();
        assert_eq!(
            manifest.permissions,
            vec![
                PermissionDeclaration {
                    permission: Permission::Chat,
                    note: "Reads chat.".into(),
                    hosts: vec![],
                },
                PermissionDeclaration {
                    permission: Permission::Network,
                    note: "Talks to its backend.".into(),
                    hosts: vec!["example.org".into()],
                },
            ]
        );
        assert!(manifest.allows(Permission::Chat));
        assert!(!manifest.allows(Permission::Clipboard));
    }

    #[test]
    fn permission_rules() {
        let cases = [
            (
                r#", "permissions": [{ "name": "chat", "note": "n" }]"#,
                "permissions requires permissionsVersion >= 1".to_owned(),
            ),
            (
                r#", "permissionsVersion": 2, "permissions": [{ "name": "chat", "note": "n" }]"#,
                "permissionsVersion 2 is newer than this client's vocabulary (1)".to_owned(),
            ),
            (
                r#", "permissionsVersion": 1, "permissions": [{ "name": "fileWrite", "note": "n" }]"#,
                "unknown permission: fileWrite".to_owned(),
            ),
            (
                r#", "permissionsVersion": 1, "permissions": [{ "name": "chat", "note": "n" }, { "name": "CHAT", "note": "n" }]"#,
                "duplicate permission: CHAT".to_owned(),
            ),
            (
                r#", "permissionsVersion": 1, "permissions": [{ "name": "chat" }]"#,
                "permission 'chat' is missing a note".to_owned(),
            ),
            (
                r#", "permissionsVersion": 1, "permissions": [{ "name": "chat", "note": "see https://x" }]"#,
                "permission 'chat' note contains a link".to_owned(),
            ),
            (
                r#", "permissionsVersion": 1, "permissions": [{ "name": "chat", "note": "a\u200Bb" }]"#,
                "permission 'chat' note contains a disallowed formatting character".to_owned(),
            ),
            (
                r#", "permissionsVersion": 1, "permissions": [{ "name": "chat", "note": "a\u0007b" }]"#,
                "permission 'chat' note contains a control character".to_owned(),
            ),
            (
                &format!(
                    r#", "permissionsVersion": 1, "permissions": [{{ "name": "chat", "note": "{}" }}]"#,
                    "n".repeat(121)
                ),
                "permission 'chat' note exceeds 120 characters".to_owned(),
            ),
            (
                r#", "permissionsVersion": 1, "permissions": [{ "name": "chat", "note": "n", "hosts": ["x"] }]"#,
                "permission 'chat' takes no hosts (only network does)".to_owned(),
            ),
        ];
        for (extra, expected) in cases {
            assert_eq!(parse(&with(extra)), Err(expected), "{extra}");
        }
    }

    #[test]
    fn the_memory_limit_defaults_and_is_capped() {
        assert_eq!(
            parse(&with(r#", "limits": { "memoryMiB": 64 }"#)).map(|m| m.memory_mib),
            Ok(64)
        );
        assert_eq!(
            parse(&with(r#", "limits": {}"#)).map(|m| m.memory_mib),
            Ok(DEFAULT_MEMORY_MIB)
        );
        for bad in ["0", "1025", "1.5", "\"64\""] {
            assert_eq!(
                parse(&with(&format!(r#", "limits": {{ "memoryMiB": {bad} }}"#))),
                Err("limits.memoryMiB must be a whole number from 1 to 1024".into()),
                "{bad}"
            );
        }
    }
}
