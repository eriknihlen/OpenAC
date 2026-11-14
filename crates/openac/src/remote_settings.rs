//! Where a session's remote-window settings are kept between runs, for
//! `/remoteac`.
//!
//! A session whose document has a `remote` block keeps them there. Every
//! other session keeps them in the client's settings folder: the launcher
//! writes a new document for every start, so a setting written into it
//! would not outlive the run.
//!
//! The relay's address and its token are the install's, shared by every
//! session of it: `<config>/remote/relay.json` holds the address and where
//! the token is (never the token), and the token is
//! `<config>/remote/relay.token`, readable only by its owner, read as any
//! `file` credential is. Whether a session connects is its own:
//! `<config>/remote/<server>/<account>/<character>.json`. Many clients of
//! one install are often told the same command at once, so each value is
//! written only by the command that sets it, and read again before it is
//! used.

use std::io::Write as _;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use ac_host::json::{self, Member, Node, Value};
use ac_host::{
    CharacterSelector, CredentialProvider, CredentialReference, SessionDescriptor, SessionMode,
};

/// The folder of the remote settings in the config directory.
pub(crate) const REMOTE_FOLDER: &str = "remote";
/// The install's relay settings, relative to the config directory.
pub(crate) const RELAY_SETTINGS: &str = "remote/relay.json";
/// The install's relay token, relative to the config directory.
pub(crate) const RELAY_TOKEN: &str = "remote/relay.token";
/// The settings files' format.
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

/// Which session a switch is: its server, its account and how it names its
/// character.
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

    /// The session's switch file, relative to the config directory.
    pub(crate) fn switch_reference(&self) -> String {
        format!(
            "{REMOTE_FOLDER}/{}/{}/{}.json",
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
    /// In the settings folder: the install's relay, the session's switch.
    Store {
        /// The install's relay settings.
        relay: PathBuf,
        /// The session's switch.
        switch: PathBuf,
    },
}

impl Place {
    /// Where `session`'s settings live, and what they are now. The
    /// document's block wins; without one the settings folder's; without
    /// those, nothing is set and nothing connects.
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
        let place = Self::Store {
            relay: config_directory.join(RELAY_SETTINGS),
            switch: config_directory.join(StoreKey::of(session).switch_reference()),
        };
        let settings = place.reload(&Settings::default());
        (place, settings)
    }

    /// The settings as they stand now: another client of the install may
    /// have set the relay since. A document's block is this session's
    /// alone: `current` is what it holds.
    pub(crate) fn reload(&self, current: &Settings) -> Result<Settings, String> {
        match self {
            Self::Document { .. } => Ok(current.clone()),
            Self::Store { relay, switch } => {
                let relay = read_file(relay)?;
                let switch = read_file(switch)?;
                let shared = relay.as_deref().and_then(read_relay);
                Ok(Settings {
                    url: shared.as_ref().and_then(|s| s.0.clone()),
                    credential: shared.and_then(|s| s.1),
                    enabled: switch.as_deref().is_some_and(read_switch),
                })
            }
        }
    }

    /// Where the settings are kept, for `/remoteac status`.
    pub(crate) fn describe(&self) -> String {
        match self {
            Self::Document { path, .. } => format!("the session document {}", path.display()),
            Self::Store { relay, switch } => {
                format!("{} and {}", relay.display(), switch.display())
            }
        }
    }

    /// Keep the relay's address and token reference of `settings`: in the
    /// settings folder, for every session of the install.
    pub(crate) fn save_relay(&self, settings: &Settings) -> Result<(), String> {
        match self {
            Self::Document { path, session_id } => save_document(path, session_id, settings),
            Self::Store { relay, .. } => {
                // A file that cannot be read is never written over.
                read_file(relay)?;
                let mut members = vec![version_member()];
                members.extend(relay_members(settings));
                write_json(relay, &Node::made(Value::Object(members)))
            }
        }
    }

    /// Keep whether this session connects.
    pub(crate) fn save_switch(&self, settings: &Settings) -> Result<(), String> {
        match self {
            Self::Document { path, session_id } => save_document(path, session_id, settings),
            Self::Store { switch, .. } => {
                read_file(switch)?;
                let members = vec![
                    version_member(),
                    Member::made("enabled", Value::Bool(settings.enabled)),
                ];
                write_json(switch, &Node::made(Value::Object(members)))
            }
        }
    }
}

// ---------------------------------------------------------------------------
// The settings folder's files.

/// The members of the settings file at `path`, checked for its format;
/// none when there is no such file.
fn read_file(path: &Path) -> Result<Option<Vec<Member>>, String> {
    let bytes = match std::fs::read(path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(format!("{} cannot be read: {error}", path.display())),
    };
    let node =
        json::parse(&bytes).map_err(|error| format!("{} is not JSON: {error}", path.display()))?;
    let members = json::object_members(&node)
        .map_err(|_| format!("{} is not a remote settings file", path.display()))?;
    let version = member(members, "version").and_then(|v| json::uint32(v).ok());
    if version != Some(STORE_VERSION) {
        return Err(format!(
            "{} has format {version:?}; this client reads format {STORE_VERSION}",
            path.display()
        ));
    }
    Ok(Some(members.to_vec()))
}

fn version_member() -> Member {
    Member::made("version", Value::Number(STORE_VERSION.to_string()))
}

/// The address and token reference of the relay settings.
fn read_relay(members: &[Member]) -> Option<(Option<String>, Option<CredentialReference>)> {
    let credential = match member(members, "credential") {
        Some(node) if !json::is_null(node) => Some(read_credential(node)?),
        _ => None,
    };
    Some((text(members, "url"), credential))
}

/// Whether a session's switch is on.
fn read_switch(members: &[Member]) -> bool {
    member(members, "enabled").is_some_and(|e| json::boolean(e) == Ok(true))
}

fn member<'a>(members: &'a [Member], name: &str) -> Option<&'a Node> {
    members.iter().find(|m| m.name == name).map(|m| &m.value)
}

fn text(members: &[Member], name: &str) -> Option<String> {
    member(members, name).and_then(|node| json::string_or_null(node).ok().flatten())
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

/// The address and the token reference: never the token.
fn relay_members(settings: &Settings) -> Vec<Member> {
    let credential = settings.credential.as_ref().map_or(Value::Null, |c| {
        Value::Object(vec![
            Member::made(
                "provider",
                Value::String(provider_name(c.provider).to_owned()),
            ),
            Member::made("reference", Value::String(c.reference.clone())),
        ])
    });
    vec![
        Member::made(
            "url",
            settings.url.clone().map_or(Value::Null, Value::String),
        ),
        Member::made("credential", credential),
    ]
}

/// The members a document's remote block holds: never the token.
fn remote_members(settings: &Settings) -> Vec<Member> {
    let mut members = relay_members(settings);
    members.insert(1, Member::made("enabled", Value::Bool(settings.enabled)));
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
    // Named for this process and this write: two writers never share it.
    static WRITES: AtomicU64 = AtomicU64::new(0);
    let mut beside = path.as_os_str().to_owned();
    beside.push(format!(
        ".{}-{}.new",
        std::process::id(),
        WRITES.fetch_add(1, Ordering::Relaxed)
    ));
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
    fn each_character_has_its_own_switch_file() {
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
            a.switch_reference(),
            "remote/play_example_9000/acct/name_A_guy____x.json"
        );
        let references = [a, b, c].map(|k| k.switch_reference());
        assert_ne!(references[0], references[1]);
        assert_ne!(references[1], references[2]);
    }

    #[test]
    fn the_relay_is_the_installs_and_the_switch_each_sessions() {
        let dir = scratch("store");
        let document = dir.join("session.json");
        let first = session(Some(CharacterSelector::Name("One".into())));
        let second = session(Some(CharacterSelector::Name("Two".into())));
        // Nothing stored: nothing set, nothing on.
        let (one, settings) = Place::find(&first, &document, &dir);
        assert_eq!(settings, Ok(Settings::default()));
        let (two, _) = Place::find(&second, &document, &dir);
        // One session sets the relay: the other sees it too.
        let relay = set("wss://relay.example/a", RELAY_TOKEN, false);
        one.save_relay(&relay).unwrap();
        write_token(&dir, RELAY_TOKEN, "hunter2-token").unwrap();
        assert_eq!(two.reload(&Settings::default()), Ok(relay.clone()));
        // A switch is the session's own.
        let on = Settings {
            enabled: true,
            ..relay.clone()
        };
        one.save_switch(&on).unwrap();
        assert_eq!(one.reload(&Settings::default()), Ok(on));
        assert_eq!(two.reload(&Settings::default()), Ok(relay));
        // Neither file holds the token, and the switch holds no relay.
        let shared = std::fs::read_to_string(dir.join(RELAY_SETTINGS)).unwrap();
        assert!(!shared.contains("hunter2"), "{shared}");
        let own = dir.join(StoreKey::of(&first).switch_reference());
        let own = std::fs::read_to_string(own).unwrap();
        assert!(!own.contains("hunter2") && !own.contains("url"), "{own}");
        let token = std::fs::read_to_string(dir.join(RELAY_TOKEN));
        assert_eq!(token.unwrap(), "hunter2-token");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn sessions_told_the_same_commands_at_once_all_keep_them() {
        // One install's clients, all told the same commands at once.
        let dir = scratch("at-once");
        let document = dir.join("session.json");
        let threads: Vec<_> = (0..24)
            .map(|n| {
                let dir = dir.clone();
                let document = document.clone();
                std::thread::spawn(move || {
                    let me = session(Some(CharacterSelector::Id(0x5000_0000 + n)));
                    let (place, settings) = Place::find(&me, &document, &dir);
                    settings.unwrap();
                    let relay = set("wss://relay.example/ws", RELAY_TOKEN, false);
                    place.save_relay(&relay).unwrap();
                    write_token(&dir, RELAY_TOKEN, "shared-token").unwrap();
                    let on = Settings {
                        enabled: true,
                        ..place.reload(&Settings::default()).unwrap()
                    };
                    place.save_switch(&on).unwrap();
                })
            })
            .collect();
        for thread in threads {
            thread.join().unwrap();
        }
        let expected = set("wss://relay.example/ws", RELAY_TOKEN, true);
        for n in 0..24 {
            let me = session(Some(CharacterSelector::Id(0x5000_0000 + n)));
            assert_eq!(
                Place::find(&me, &document, &dir).1,
                Ok(expected.clone()),
                "{n}"
            );
        }
        let token = std::fs::read_to_string(dir.join(RELAY_TOKEN));
        assert_eq!(token.unwrap(), "shared-token");
        // No file is left half-written beside them.
        let leftovers: Vec<_> = walk(&dir)
            .into_iter()
            .filter(|p| p.extension().is_some_and(|e| e == "new"))
            .collect();
        assert!(leftovers.is_empty(), "{leftovers:?}");
        let _ = std::fs::remove_dir_all(&dir);
    }

    fn walk(dir: &Path) -> Vec<PathBuf> {
        let mut found = Vec::new();
        for entry in std::fs::read_dir(dir).unwrap().flatten() {
            let path = entry.path();
            if path.is_dir() {
                found.extend(walk(&path));
            } else {
                found.push(path);
            }
        }
        found
    }

    #[cfg(unix)]
    #[test]
    fn the_token_file_is_readable_only_by_its_owner() {
        use std::os::unix::fs::PermissionsExt as _;
        let dir = scratch("mode");
        write_token(&dir, RELAY_TOKEN, "t").unwrap();
        let mode = std::fs::metadata(dir.join(RELAY_TOKEN))
            .unwrap()
            .permissions()
            .mode();
        assert_eq!(mode & 0o077, 0, "{mode:o}");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_settings_file_that_cannot_be_read_is_never_overwritten() {
        let dir = scratch("broken");
        let first = session(None);
        let path = dir.join(RELAY_SETTINGS);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, "{ not json").unwrap();
        let (place, settings) = Place::find(&first, &dir.join("d.json"), &dir);
        assert!(settings.is_err());
        assert!(
            place
                .save_relay(&set("wss://a.example/", "r", true))
                .is_err()
        );
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
            .save_switch(&set("wss://new.example/ws", "remote/t.token", false))
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
        assert!(place.save_relay(&Settings::default()).is_err());
        let _ = std::fs::remove_dir_all(&dir);
    }
}
