//! Where a session's remote-window settings are kept between runs, for
//! `/remoteac`.
//!
//! A session whose document has a `remote` block keeps them there. Every
//! other session keeps them in the client's own store,
//! `<config>/remote.json`, by server, account and character: the launcher
//! writes a new document for every start, so a setting written into it
//! would not outlive the run. The store holds the address, the switch and
//! where the token is, never the token. The token is a file of its own,
//! `<config>/remote/<server>/<account>/<character>.token`, readable only by
//! its owner, read as any `file` credential is.

use std::io::Write as _;
use std::path::{Path, PathBuf};

use ac_host::json::{self, Member, Node, Value};
use ac_host::{
    CharacterSelector, CredentialProvider, CredentialReference, SessionDescriptor, SessionMode,
};

/// The store's file name in the config directory.
pub(crate) const STORE_FILE_NAME: &str = "remote.json";
/// The folder of the tokens in the config directory.
pub(crate) const TOKEN_FOLDER: &str = "remote";
/// The store's format.
const STORE_VERSION: u32 = 1;

/// What a session's remote window is set to.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub(crate) struct Settings {
    /// The relay's address, as set.
    pub(crate) url: Option<String>,
    /// Where the token comes from.
    pub(crate) credential: Option<CredentialReference>,
    /// Whether the session connects.
    pub(crate) enabled: bool,
}

/// Which session the store's entry is: its server, its account and how it
/// names its character.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct StoreKey {
    pub(crate) server: String,
    pub(crate) account: String,
    pub(crate) character: String,
}

impl StoreKey {
    /// The key of `session`.
    pub(crate) fn of(session: &SessionDescriptor) -> Self {
        let character = match &session.mode {
            SessionMode::Play {
                character: Some(selector),
                ..
            } => match selector {
                CharacterSelector::Name(name) => format!("name:{name}"),
                CharacterSelector::Id(id) => format!("id:0x{id:08X}"),
                CharacterSelector::Index(index) => format!("index:{index}"),
            },
            // The player picks the character at the character screen.
            SessionMode::Play {
                character: None, ..
            }
            | SessionMode::Probe => "choose".to_owned(),
        };
        Self {
            server: format!("{}:{}", session.endpoint.host, session.endpoint.port),
            account: session.account.clone(),
            character,
        }
    }

    /// The token file's reference, relative to the config directory: one
    /// file per key, so each session's token can be replaced or revoked on
    /// its own.
    pub(crate) fn token_reference(&self) -> String {
        format!(
            "{TOKEN_FOLDER}/{}/{}/{}.token",
            file_name_part(&self.server),
            file_name_part(&self.account),
            file_name_part(&self.character)
        )
    }
}

/// `text` as one part of a file name: ASCII letters, digits and `-` kept,
/// anything else `_` (no separators, no `..`).
fn file_name_part(text: &str) -> String {
    let part: String = text
        .chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || c == '-' {
                c
            } else {
                '_'
            }
        })
        .collect();
    if part.is_empty() {
        "_".to_owned()
    } else {
        part
    }
}

/// Where a session's settings live.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum Place {
    /// In the session's own document, which has a `remote` block.
    Document {
        /// The document.
        path: PathBuf,
        /// The session's id in it.
        session_id: String,
    },
    /// In the client's store.
    Store {
        /// The store file.
        path: PathBuf,
        /// The session's entry.
        key: StoreKey,
    },
}

impl Place {
    /// Where `session`'s settings live, and what they are now. The
    /// document's block wins; without one the store's entry; without that,
    /// nothing is set and nothing connects.
    pub(crate) fn find(
        session: &SessionDescriptor,
        document: &Path,
        config_directory: &Path,
    ) -> (Self, Result<Settings, String>) {
        if let Some(remote) = &session.remote {
            let place = Self::Document {
                path: document.to_path_buf(),
                session_id: session.id.clone(),
            };
            let settings = Settings {
                url: Some(remote.url.clone()),
                credential: Some(remote.credential.clone()),
                enabled: remote.enabled,
            };
            return (place, Ok(settings));
        }
        let key = StoreKey::of(session);
        let path = config_directory.join(STORE_FILE_NAME);
        let settings = read_store(&path).map(|entries| {
            entries
                .into_iter()
                .find(|(k, _)| *k == key)
                .map(|(_, settings)| settings)
                .unwrap_or_default()
        });
        (Self::Store { path, key }, settings)
    }

    /// Where the settings are kept, for `/remoteac status`.
    pub(crate) fn describe(&self) -> String {
        match self {
            Self::Document { path, .. } => format!("the session document {}", path.display()),
            Self::Store { path, .. } => path.display().to_string(),
        }
    }

    /// Keep `settings` here.
    pub(crate) fn save(&self, settings: &Settings) -> Result<(), String> {
        match self {
            Self::Document { path, session_id } => save_document(path, session_id, settings),
            Self::Store { path, key } => save_store(path, key, settings),
        }
    }
}

// ---------------------------------------------------------------------------
// The store.

/// Every entry of the store at `path`; none when there is no store.
fn read_store(path: &Path) -> Result<Vec<(StoreKey, Settings)>, String> {
    let bytes = match std::fs::read(path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(error) => return Err(format!("{} cannot be read: {error}", path.display())),
    };
    let node =
        json::parse(&bytes).map_err(|error| format!("{} is not JSON: {error}", path.display()))?;
    let unreadable = || format!("{} is not a remote settings store", path.display());
    let members = json::object_members(&node).map_err(|_| unreadable())?;
    let version = member(members, "version").and_then(|v| json::uint32(v).ok());
    if version != Some(STORE_VERSION) {
        return Err(format!(
            "{} has format {version:?}; this client reads format {STORE_VERSION}",
            path.display()
        ));
    }
    let sessions = member(members, "sessions")
        .and_then(|s| json::array_items(s).ok())
        .ok_or_else(unreadable)?;
    sessions
        .iter()
        .map(|entry| read_entry(entry).ok_or_else(unreadable))
        .collect()
}

fn member<'a>(members: &'a [Member], name: &str) -> Option<&'a Node> {
    members.iter().find(|m| m.name == name).map(|m| &m.value)
}

fn text(members: &[Member], name: &str) -> Option<String> {
    member(members, name).and_then(|node| json::string_or_null(node).ok().flatten())
}

fn read_entry(node: &Node) -> Option<(StoreKey, Settings)> {
    let members = json::object_members(node).ok()?;
    let key = StoreKey {
        server: text(members, "server")?,
        account: text(members, "account")?,
        character: text(members, "character")?,
    };
    let credential = match member(members, "credential") {
        Some(node) if !json::is_null(node) => Some(read_credential(node)?),
        _ => None,
    };
    let settings = Settings {
        url: text(members, "url"),
        credential,
        enabled: member(members, "enabled").is_some_and(|e| json::boolean(e) == Ok(true)),
    };
    Some((key, settings))
}

fn read_credential(node: &Node) -> Option<CredentialReference> {
    let members = json::object_members(node).ok()?;
    let provider = match text(members, "provider")?.as_str() {
        "file" => CredentialProvider::File,
        "environment" => CredentialProvider::Environment,
        // A token typed at standard input cannot be read again later.
        _ => return None,
    };
    Some(CredentialReference {
        provider,
        reference: text(members, "reference")?,
    })
}

fn save_store(path: &Path, key: &StoreKey, settings: &Settings) -> Result<(), String> {
    let mut entries = read_store(path)?;
    match entries.iter_mut().find(|(k, _)| k == key) {
        Some(entry) => entry.1 = settings.clone(),
        None => entries.push((key.clone(), settings.clone())),
    }
    let sessions = entries
        .iter()
        .map(|(key, settings)| {
            let mut members = vec![
                Member::made("server", Value::String(key.server.clone())),
                Member::made("account", Value::String(key.account.clone())),
                Member::made("character", Value::String(key.character.clone())),
            ];
            members.extend(remote_members(settings));
            Node::made(Value::Object(members))
        })
        .collect();
    let store = Node::made(Value::Object(vec![
        Member::made("version", Value::Number(STORE_VERSION.to_string())),
        Member::made("sessions", Value::Array(sessions)),
    ]));
    write_json(path, &store)
}

/// The members a remote block or a store entry holds: never the token.
fn remote_members(settings: &Settings) -> Vec<Member> {
    let mut members = vec![
        Member::made(
            "url",
            settings.url.clone().map_or(Value::Null, Value::String),
        ),
        Member::made("enabled", Value::Bool(settings.enabled)),
    ];
    let credential = settings.credential.as_ref().map_or(Value::Null, |c| {
        Value::Object(vec![
            Member::made(
                "provider",
                Value::String(provider_name(c.provider).to_owned()),
            ),
            Member::made("reference", Value::String(c.reference.clone())),
        ])
    });
    members.push(Member::made("credential", credential));
    members
}

fn provider_name(provider: CredentialProvider) -> &'static str {
    match provider {
        CredentialProvider::Environment => "environment",
        CredentialProvider::StandardInput => "standardInput",
        CredentialProvider::File => "file",
    }
}

// ---------------------------------------------------------------------------
// The document.

/// Write `settings` into the `remote` block of session `session_id` of the
/// document at `path`, leaving everything else as it was. Only a block with
/// an address and a token is written: the document's reader refuses
/// anything less.
fn save_document(path: &Path, session_id: &str, settings: &Settings) -> Result<(), String> {
    let (Some(_), Some(_)) = (&settings.url, &settings.credential) else {
        return Err("a session document's remote block needs both a url and a secret".to_owned());
    };
    let bytes = std::fs::read(path)
        .map_err(|error| format!("{} cannot be read: {error}", path.display()))?;
    let mut document =
        json::parse(&bytes).map_err(|error| format!("{} is not JSON: {error}", path.display()))?;
    let session = session_node(&mut document, session_id)
        .ok_or_else(|| format!("{} names no session '{session_id}'", path.display()))?;
    let Value::Object(members) = &mut session.value else {
        return Err(format!(
            "{} names no session '{session_id}'",
            path.display()
        ));
    };
    let block = Value::Object(remote_members(settings));
    match members.iter_mut().find(|m| m.name == "remote") {
        Some(remote) => remote.value = Node::made(block),
        None => members.push(Member::made("remote", block)),
    }
    write_json(path, &document)
}

fn session_node<'a>(document: &'a mut Node, session_id: &str) -> Option<&'a mut Node> {
    let Value::Object(members) = &mut document.value else {
        return None;
    };
    let sessions = members.iter_mut().find(|m| m.name == "sessions")?;
    let Value::Array(sessions) = &mut sessions.value.value else {
        return None;
    };
    sessions.iter_mut().find(|session| {
        json::object_members(session)
            .is_ok_and(|members| text(members, "id").as_deref() == Some(session_id))
    })
}

/// Replace `path` with `node`: written beside it, then renamed over it, so
/// a crash leaves the old file or the new one, never half of one.
fn write_json(path: &Path, node: &Node) -> Result<(), String> {
    let mut out = String::new();
    json::write_value(&mut out, node, 0);
    out.push('\n');
    replace_file(path, out.as_bytes(), false)
        .map_err(|error| format!("{} cannot be written: {error}", path.display()))
}

/// Replace `path` with `bytes` through a file beside it; `private` makes
/// the file readable only by its owner (on Unix; on Windows the folder's
/// own access list applies).
fn replace_file(path: &Path, bytes: &[u8], private: bool) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let mut beside = path.as_os_str().to_owned();
    beside.push(".new");
    let beside = PathBuf::from(beside);
    let mut options = std::fs::OpenOptions::new();
    options.write(true).create(true).truncate(true);
    #[cfg(unix)]
    if private {
        use std::os::unix::fs::OpenOptionsExt as _;
        options.mode(0o600);
    }
    #[cfg(not(unix))]
    let _ = private;
    let written = options.open(&beside).and_then(|mut file| {
        file.write_all(bytes)?;
        file.sync_all()
    });
    if let Err(error) = written.and_then(|()| std::fs::rename(&beside, path)) {
        let _ = std::fs::remove_file(&beside);
        return Err(error);
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// The token.

/// Write `token` as the file `reference` names under `config_directory`,
/// readable only by its owner.
pub(crate) fn write_token(
    config_directory: &Path,
    reference: &str,
    token: &str,
) -> Result<(), String> {
    let path = config_directory.join(reference);
    // A file left by another program may carry group or other bits: the
    // new one replaces it with the owner's alone.
    replace_file(&path, token.as_bytes(), true).map_err(|error| {
        format!(
            "the secret cannot be written to {}: {error}",
            path.display()
        )
    })
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use ac_host::Endpoint;
    use ac_host::config::RemoteDescriptor;

    /// An empty folder of its own under the system's temporary folder.
    pub(crate) fn scratch(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "openac-remote-settings-{name}-{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    pub(crate) fn session(selector: Option<CharacterSelector>) -> SessionDescriptor {
        SessionDescriptor {
            id: "main".into(),
            endpoint: Endpoint {
                host: "play.example".into(),
                port: 9000,
            },
            account: "acct".into(),
            mode: SessionMode::Play {
                character: selector,
                policy: None,
            },
            credential: CredentialReference {
                provider: CredentialProvider::StandardInput,
                reference: "password".into(),
            },
            character_options: None,
            plugins: None,
            plugin_tags: None,
            plugin_settings: None,
            login_commands: None,
            login_command_delay_ms: 0,
            status_file: None,
            remote: None,
        }
    }

    fn set(url: &str, reference: &str, enabled: bool) -> Settings {
        Settings {
            url: Some(url.into()),
            credential: Some(CredentialReference {
                provider: CredentialProvider::File,
                reference: reference.into(),
            }),
            enabled,
        }
    }

    #[test]
    fn each_character_has_its_own_entry_and_token_file() {
        let a = StoreKey::of(&session(Some(CharacterSelector::Name(
            "A guy/..\\x".into(),
        ))));
        let b = StoreKey::of(&session(Some(CharacterSelector::Id(0x5000_0001))));
        let c = StoreKey::of(&session(None));
        assert_eq!(a.server, "play.example:9000");
        assert_eq!(a.character, "name:A guy/..\\x");
        assert_eq!(b.character, "id:0x50000001");
        assert_eq!(c.character, "choose");
        assert_eq!(
            a.token_reference(),
            "remote/play_example_9000/acct/name_A_guy____x.token"
        );
        let references = [a, b, c].map(|k| k.token_reference());
        assert_ne!(references[0], references[1]);
        assert_ne!(references[1], references[2]);
    }

    #[test]
    fn the_store_keeps_each_session_and_never_the_token() {
        let dir = scratch("store");
        let document = dir.join("session.json");
        let first = session(Some(CharacterSelector::Name("One".into())));
        let second = session(Some(CharacterSelector::Name("Two".into())));
        // Nothing stored: nothing set, nothing on.
        let (place, settings) = Place::find(&first, &document, &dir);
        assert_eq!(settings, Ok(Settings::default()));
        let one = set(
            "wss://relay.example/a",
            &StoreKey::of(&first).token_reference(),
            true,
        );
        place.save(&one).unwrap();
        write_token(
            &dir,
            &StoreKey::of(&first).token_reference(),
            "hunter2-token",
        )
        .unwrap();
        let (place2, _) = Place::find(&second, &document, &dir);
        let two = set("wss://relay.example/b", "remote/b.token", false);
        place2.save(&two).unwrap();
        // Read back, each its own.
        assert_eq!(Place::find(&first, &document, &dir).1, Ok(one.clone()));
        assert_eq!(Place::find(&second, &document, &dir).1, Ok(two));
        // Saved again: replaced, not added.
        let off = Settings {
            enabled: false,
            ..one
        };
        place.save(&off).unwrap();
        assert_eq!(Place::find(&first, &document, &dir).1, Ok(off));
        let store = std::fs::read_to_string(dir.join(STORE_FILE_NAME)).unwrap();
        assert_eq!(store.matches("\"server\"").count(), 2, "{store}");
        assert!(!store.contains("hunter2"), "{store}");
        let token = std::fs::read_to_string(dir.join(StoreKey::of(&first).token_reference()));
        assert_eq!(token.unwrap(), "hunter2-token");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[cfg(unix)]
    #[test]
    fn the_token_file_is_readable_only_by_its_owner() {
        use std::os::unix::fs::PermissionsExt as _;
        let dir = scratch("mode");
        write_token(&dir, "remote/x/y.token", "t").unwrap();
        let mode = std::fs::metadata(dir.join("remote/x/y.token"))
            .unwrap()
            .permissions()
            .mode();
        assert_eq!(mode & 0o077, 0, "{mode:o}");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_store_that_cannot_be_read_is_never_overwritten() {
        let dir = scratch("broken");
        let path = dir.join(STORE_FILE_NAME);
        std::fs::write(&path, "{ not json").unwrap();
        let first = session(None);
        let (place, settings) = Place::find(&first, &dir.join("d.json"), &dir);
        assert!(settings.is_err());
        assert!(place.save(&set("wss://a.example/", "r", true)).is_err());
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "{ not json");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_document_with_a_remote_block_keeps_its_settings_and_everything_else() {
        let dir = scratch("document");
        let path = dir.join("session.json");
        let text = r#"{
  "version": 1,
  "sessions": [
    { "id": "other", "endpoint": { "host": "h", "port": 1 } },
    {
      "id": "main",
      "loginCommandDelayMs": 0.50,
      "remote": {
        "url": "wss://old.example/ws",
        "credential": { "provider": "environment", "reference": "RELAY_TOKEN" }
      }
    }
  ]
}"#;
        std::fs::write(&path, text).unwrap();
        let mut main = session(None);
        main.remote = Some(RemoteDescriptor {
            url: "wss://old.example/ws".into(),
            credential: CredentialReference {
                provider: CredentialProvider::Environment,
                reference: "RELAY_TOKEN".into(),
            },
            enabled: true,
        });
        let (place, settings) = Place::find(&main, &path, &dir);
        assert!(matches!(place, Place::Document { .. }));
        assert!(settings.unwrap().enabled);
        place
            .save(&set("wss://new.example/ws", "remote/t.token", false))
            .unwrap();
        let written = json::parse(&std::fs::read(&path).unwrap()).unwrap();
        let mut out = String::new();
        json::write_value(&mut out, &written, 0);
        assert!(out.contains("\"loginCommandDelayMs\": 0.50"), "{out}");
        assert!(out.contains("\"id\": \"other\""), "{out}");
        assert!(out.contains("wss://new.example/ws"), "{out}");
        assert!(out.contains("\"enabled\": false"), "{out}");
        assert!(!out.contains("RELAY_TOKEN"), "{out}");
        // A block without both a url and a secret is never written.
        assert!(place.save(&Settings::default()).is_err());
        let _ = std::fs::remove_dir_all(&dir);
    }
}
