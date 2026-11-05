//! The session-config document: a versioned, strict JSON file naming the
//! sessions a process runs and how each one logs in.
//!
//! Reading happens in two passes, as two different kinds of mistake: first
//! the document must have the right shape (only known members, each at most
//! once, required members present, every value of its type); then the
//! values must make sense together (non-empty ids, a port in range, exactly
//! one character selector, and so on). Either kind is a configuration error;
//! only the second kind names values from the document.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use crate::character_options::{self, CharacterOption};
use crate::json::{self, JsonError, Node, ObjectReader};
use crate::paths::PathOverrides;

/// The one document version this reader understands.
pub const CONFIG_VERSION: i32 = 1;

/// Milliseconds between login commands when the document names none.
pub const DEFAULT_LOGIN_COMMAND_DELAY_MS: u32 = 500;

/// Why a document cannot be used.
#[derive(Debug, thiserror::Error)]
pub enum ConfigError {
    /// The file could not be read.
    #[error("could not read '{}': {source}", .path.display())]
    Read {
        /// The file.
        path: PathBuf,
        /// The failure.
        source: std::io::Error,
    },
    /// The document is not the right shape. Says where, never what.
    #[error("the JSON document is invalid ({0})")]
    Json(#[from] JsonError),
    /// The values do not make sense; the text says which and why.
    #[error("{0}")]
    Invalid(String),
}

/// Which host reads the document; they share the format but differ in what
/// they require.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConfigReader {
    /// The windowless host: any number of sessions; each normal session
    /// names a character and a bot policy; `mode: "probe"` allowed.
    Headless,
    /// A window started on a document: exactly one session; the character is
    /// optional (absent means the player picks); `policy` is accepted and
    /// ignored; `mode: "probe"` is refused.
    Window,
}

/// A checked document.
#[derive(Debug, Clone, PartialEq)]
pub struct HostConfig {
    /// Process-wide settings.
    pub process: ProcessSettings,
    /// The sessions, in document order.
    pub sessions: Vec<SessionDescriptor>,
}

impl HostConfig {
    /// The document version (always [`CONFIG_VERSION`]).
    pub const fn version(&self) -> i32 {
        CONFIG_VERSION
    }
}

/// Settings shared by every session in the process.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ProcessSettings {
    /// Directory overrides (`process.paths`).
    pub paths: PathOverrides,
    /// Game content the process reads (`process.content`).
    pub content: Option<ContentDescriptor>,
}

/// Where the process's game content is.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ContentDescriptor {
    /// The retail dat directory (`datDirectory`).
    pub dat_directory: PathBuf,
    /// A prepared asset package (`preparedAssetPath`). Required by the
    /// format; not read by this client yet.
    pub prepared_asset_path: PathBuf,
    /// A layered update package over it, if any.
    pub prepared_asset_overlay: Option<PreparedAssetOverlay>,
}

/// A prepared-asset overlay: path and the two recipe versions, always together.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PreparedAssetOverlay {
    /// `preparedAssetOverlayPath`.
    pub path: PathBuf,
    /// `preparedAssetBaseRecipeVersion`, above zero.
    pub base_recipe_version: u32,
    /// `preparedAssetEffectiveRecipeVersion`, above zero.
    pub effective_recipe_version: u32,
}

/// One session.
#[derive(Debug, Clone, PartialEq)]
pub struct SessionDescriptor {
    /// Unique within the document.
    pub id: String,
    /// The login server.
    pub endpoint: Endpoint,
    /// The account name.
    pub account: String,
    /// Normal play or a probe.
    pub mode: SessionMode,
    /// Where the password comes from.
    pub credential: CredentialReference,
    /// Character options to set on login (`characterOptions`); `None` when
    /// the document declares none, which is not the same as an empty block.
    pub character_options: Option<BTreeMap<CharacterOption, bool>>,
    /// Plugin allow-list in load order (`plugins`): `None` loads all, empty
    /// loads none.
    pub plugins: Option<Vec<String>>,
    /// Words the session wants to be found by (`pluginTags`).
    pub plugin_tags: Option<Vec<String>>,
    /// Startup settings per plugin id (`pluginSettings`).
    pub plugin_settings: Option<BTreeMap<String, BTreeMap<String, String>>>,
    /// Commands to run after entering the world (`loginCommands`).
    pub login_commands: Option<Vec<String>>,
    /// Milliseconds between login commands (`loginCommandDelayMs`).
    pub login_command_delay_ms: u32,
    /// Where status events go, one JSON line each (`statusFile`).
    pub status_file: Option<PathBuf>,
}

/// The login server.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Endpoint {
    /// Host name or address.
    pub host: String,
    /// Port, 1 through 65535.
    pub port: u16,
}

/// What a session does once logged in.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SessionMode {
    /// Normal play. The headless reader guarantees both fields are present;
    /// the window reader allows either to be absent.
    Play {
        /// Which character to enter the world with.
        character: Option<CharacterSelector>,
        /// Which bot policy drives the session.
        policy: Option<PolicyDescriptor>,
    },
    /// Connect, read the character list, never select a character.
    Probe,
}

/// How a session names its character; exactly one way.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CharacterSelector {
    /// Position in the server's character list, from 0.
    Index(u32),
    /// The character's object id (never 0).
    Id(u32),
    /// The character's name.
    Name(String),
}

/// The bot policy driving a windowless session.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PolicyDescriptor {
    /// The policy's id (`idle`, `lifecycle-smoke`, `portal-route-smoke`).
    pub id: String,
    /// The bot's role in a group, if it has one.
    pub role: Option<PolicyRole>,
}

/// A bot's role in a group.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PolicyRole {
    /// Leads; recruits the others.
    Leader,
    /// Gets recruited and swears to the leader.
    Recruit,
}

/// Where a session's password comes from.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CredentialReference {
    /// The provider.
    pub provider: CredentialProvider,
    /// What the provider looks up: a variable name, a label, a file path.
    pub reference: String,
}

/// A source of passwords.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum CredentialProvider {
    /// An environment variable named by the reference.
    Environment,
    /// One line of standard input; the reference is a label.
    StandardInput,
    /// A file readable only by its owner, relative to the config directory.
    File,
}

impl CredentialProvider {
    /// The provider's name.
    pub const fn name(self) -> &'static str {
        match self {
            Self::Environment => "Environment",
            Self::StandardInput => "StandardInput",
            Self::File => "File",
        }
    }
}

/// Reads and checks a document file.
pub fn load_config_file(path: &Path, reader: ConfigReader) -> Result<HostConfig, ConfigError> {
    let bytes = std::fs::read(path).map_err(|source| ConfigError::Read {
        path: path.to_path_buf(),
        source,
    })?;
    load_config(&bytes, reader)
}

/// Checks a document held in memory.
pub fn load_config(bytes: &[u8], reader: ConfigReader) -> Result<HostConfig, ConfigError> {
    let root = json::parse(bytes)?;
    let Some(raw) = RawConfig::read(&root, reader)? else {
        return Err(invalid("The configuration document is empty."));
    };
    raw.validate(reader)
}

fn invalid(message: impl Into<String>) -> ConfigError {
    ConfigError::Invalid(message.into())
}

// ---------------------------------------------------------------------------
// First pass: shape. `None` stands for an explicit `null` where the format
// allows one, or for an absent optional member.

/// A member that may be absent, `null`, or a value; the difference matters
/// for `mode`, `character` and `policy`.
#[derive(Debug)]
enum Presence<T> {
    Absent,
    Null,
    Value(T),
}

impl<T> Presence<T> {
    fn is_present(&self) -> bool {
        !matches!(self, Self::Absent)
    }

    fn value(self) -> Option<T> {
        match self {
            Self::Value(value) => Some(value),
            Self::Absent | Self::Null => None,
        }
    }
}

#[derive(Debug)]
struct RawConfig {
    version: i32,
    process: Option<RawProcess>,
    sessions: Option<Vec<Option<RawSession>>>,
}

#[derive(Debug)]
struct RawProcess {
    paths: Option<PathOverrides>,
    content: Option<RawContent>,
}

#[derive(Debug)]
struct RawContent {
    dat_directory: Option<String>,
    prepared_asset_path: Option<String>,
    overlay_path: Option<String>,
    base_recipe: Option<u32>,
    effective_recipe: Option<u32>,
}

#[derive(Debug)]
enum RawMode {
    Probe,
    Named(String),
}

#[derive(Debug)]
struct RawPolicy {
    id: Option<String>,
    role: Option<PolicyRole>,
}

#[derive(Debug)]
struct RawCharacter {
    index: Option<i32>,
    id: Option<u32>,
    name: Option<String>,
}

#[derive(Debug)]
struct RawEndpoint {
    host: Option<String>,
    port: i32,
}

type RawPluginSettings = Vec<(String, Option<Vec<(String, Option<String>)>>)>;

#[derive(Debug)]
struct RawSession {
    id: Option<String>,
    endpoint: Option<RawEndpoint>,
    account: Option<String>,
    mode: Presence<RawMode>,
    character: Presence<RawCharacter>,
    policy: Presence<RawPolicy>,
    credential: Option<(CredentialProvider, Option<String>)>,
    character_options: Option<Vec<(String, bool)>>,
    plugins: Option<Vec<Option<String>>>,
    plugin_tags: Option<Vec<Option<String>>>,
    login_commands: Option<Vec<Option<String>>>,
    login_command_delay_ms: i32,
    status_file: Option<String>,
    plugin_settings: Option<RawPluginSettings>,
}

/// Reads an optional member that may also be `null`.
fn optional<'a, T>(
    object: &mut ObjectReader<'a>,
    name: &str,
    read: impl FnOnce(&'a Node) -> Result<T, JsonError>,
) -> Result<Option<T>, JsonError> {
    match object.take(name) {
        Some(node) if !json::is_null(node) => read(node).map(Some),
        _ => Ok(None),
    }
}

/// Reads a required member that may be `null`.
fn required_nullable<'a, T>(
    object: &mut ObjectReader<'a>,
    name: &'static str,
    read: impl FnOnce(&'a Node) -> Result<T, JsonError>,
) -> Result<Option<T>, JsonError> {
    let node = object.required(name)?;
    if json::is_null(node) {
        Ok(None)
    } else {
        read(node).map(Some)
    }
}

fn presence<'a, T>(
    object: &mut ObjectReader<'a>,
    name: &str,
    read: impl FnOnce(&'a Node) -> Result<T, JsonError>,
) -> Result<Presence<T>, JsonError> {
    match object.take(name) {
        None => Ok(Presence::Absent),
        Some(node) if json::is_null(node) => Ok(Presence::Null),
        Some(node) => read(node).map(Presence::Value),
    }
}

fn string(node: &Node) -> Result<String, JsonError> {
    // Callers have already turned `null` away.
    json::string_or_null(node).map(Option::unwrap_or_default)
}

fn string_list(node: &Node) -> Result<Vec<Option<String>>, JsonError> {
    json::array_items(node)?
        .iter()
        .map(json::string_or_null)
        .collect()
}

const PROVIDERS: [(&str, CredentialProvider); 3] = [
    ("environment", CredentialProvider::Environment),
    ("standardInput", CredentialProvider::StandardInput),
    ("file", CredentialProvider::File),
];

const ROLES: [(&str, PolicyRole); 2] = [
    ("leader", PolicyRole::Leader),
    ("recruit", PolicyRole::Recruit),
];

impl RawConfig {
    fn read(root: &Node, reader: ConfigReader) -> Result<Option<Self>, JsonError> {
        if json::is_null(root) {
            return Ok(None);
        }
        let mut object = ObjectReader::new(root)?;
        let version = json::int32(object.required("version")?)?;
        let process = optional(&mut object, "process", RawProcess::read)?;
        let sessions = required_nullable(&mut object, "sessions", |node| {
            json::array_items(node)?
                .iter()
                .map(|item| {
                    if json::is_null(item) {
                        Ok(None)
                    } else {
                        RawSession::read(item, reader).map(Some)
                    }
                })
                .collect::<Result<Vec<_>, _>>()
        })?;
        object.finish()?;
        Ok(Some(Self {
            version,
            process,
            sessions,
        }))
    }
}

impl RawProcess {
    fn read(node: &Node) -> Result<Self, JsonError> {
        let mut object = ObjectReader::new(node)?;
        let paths = optional(&mut object, "paths", |node| {
            let mut paths = ObjectReader::new(node)?;
            let root_directory = optional(&mut paths, "rootDirectory", string)?;
            let config_directory = optional(&mut paths, "configDirectory", string)?;
            let data_directory = optional(&mut paths, "dataDirectory", string)?;
            let cache_directory = optional(&mut paths, "cacheDirectory", string)?;
            paths.finish()?;
            Ok(PathOverrides {
                root_directory,
                config_directory,
                data_directory,
                cache_directory,
            })
        })?;
        let content = optional(&mut object, "content", RawContent::read)?;
        object.finish()?;
        Ok(Self { paths, content })
    }
}

impl RawContent {
    fn read(node: &Node) -> Result<Self, JsonError> {
        let mut object = ObjectReader::new(node)?;
        let dat_directory = required_nullable(&mut object, "datDirectory", string)?;
        let prepared_asset_path = required_nullable(&mut object, "preparedAssetPath", string)?;
        let overlay_path = optional(&mut object, "preparedAssetOverlayPath", string)?;
        let base_recipe = optional(&mut object, "preparedAssetBaseRecipeVersion", json::uint32)?;
        let effective_recipe = optional(
            &mut object,
            "preparedAssetEffectiveRecipeVersion",
            json::uint32,
        )?;
        object.finish()?;
        Ok(Self {
            dat_directory,
            prepared_asset_path,
            overlay_path,
            base_recipe,
            effective_recipe,
        })
    }
}

impl RawSession {
    fn read(node: &Node, reader: ConfigReader) -> Result<Self, JsonError> {
        let mut object = ObjectReader::new(node)?;
        let id = required_nullable(&mut object, "id", string)?;
        let endpoint = required_nullable(&mut object, "endpoint", |node| {
            let mut endpoint = ObjectReader::new(node)?;
            let host = required_nullable(&mut endpoint, "host", string)?;
            let port = json::int32(endpoint.required("port")?)?;
            endpoint.finish()?;
            Ok(RawEndpoint { host, port })
        })?;
        let account = required_nullable(&mut object, "account", string)?;
        let mode = presence(&mut object, "mode", |node| match reader {
            ConfigReader::Headless => {
                json::enumeration(node, &[("probe", ())]).map(|()| RawMode::Probe)
            }
            ConfigReader::Window => string(node).map(RawMode::Named),
        })?;
        let character = presence(&mut object, "character", |node| {
            let mut character = ObjectReader::new(node)?;
            let index = optional(&mut character, "index", json::int32)?;
            let id = optional(&mut character, "id", json::uint32)?;
            let name = optional(&mut character, "name", string)?;
            character.finish()?;
            Ok(RawCharacter { index, id, name })
        })?;
        let policy = presence(&mut object, "policy", |node| {
            let mut policy = ObjectReader::new(node)?;
            let (id, role) = match reader {
                ConfigReader::Headless => (
                    required_nullable(&mut policy, "id", string)?,
                    optional(&mut policy, "role", |node| json::enumeration(node, &ROLES))?,
                ),
                // Accepted and ignored: the player drives a windowed session.
                ConfigReader::Window => {
                    let id = optional(&mut policy, "id", string)?;
                    optional(&mut policy, "role", string)?;
                    (id, None)
                }
            };
            policy.finish()?;
            Ok(RawPolicy { id, role })
        })?;
        let credential = required_nullable(&mut object, "credential", |node| {
            let mut credential = ObjectReader::new(node)?;
            let provider = json::enumeration(credential.required("provider")?, &PROVIDERS)?;
            let reference = required_nullable(&mut credential, "reference", string)?;
            credential.finish()?;
            Ok((provider, reference))
        })?;
        let character_options = optional(&mut object, "characterOptions", |node| {
            json::object_members(node)?
                .iter()
                .map(|member| Ok((member.name.clone(), json::boolean(&member.value)?)))
                .collect()
        })?;
        let plugins = optional(&mut object, "plugins", string_list)?;
        let plugin_tags = optional(&mut object, "pluginTags", string_list)?;
        let login_commands = optional(&mut object, "loginCommands", string_list)?;
        let login_command_delay_ms = match object.take("loginCommandDelayMs") {
            Some(node) => json::int32(node)?,
            None => DEFAULT_LOGIN_COMMAND_DELAY_MS as i32,
        };
        let status_file = optional(&mut object, "statusFile", string)?;
        let plugin_settings = optional(&mut object, "pluginSettings", |node| {
            json::object_members(node)?
                .iter()
                .map(|plugin| {
                    let settings = if json::is_null(&plugin.value) {
                        None
                    } else {
                        Some(
                            json::object_members(&plugin.value)?
                                .iter()
                                .map(|setting| {
                                    Ok((
                                        setting.name.clone(),
                                        json::string_or_null(&setting.value)?,
                                    ))
                                })
                                .collect::<Result<Vec<_>, JsonError>>()?,
                        )
                    };
                    Ok((plugin.name.clone(), settings))
                })
                .collect()
        })?;
        object.finish()?;
        Ok(Self {
            id,
            endpoint,
            account,
            mode,
            character,
            policy,
            credential,
            character_options,
            plugins,
            plugin_tags,
            login_commands,
            login_command_delay_ms,
            status_file,
            plugin_settings,
        })
    }
}

// ---------------------------------------------------------------------------
// Second pass: meaning.

fn blank(text: Option<&str>) -> bool {
    text.is_none_or(|text| text.trim().is_empty())
}

impl RawConfig {
    fn validate(self, reader: ConfigReader) -> Result<HostConfig, ConfigError> {
        if self.version != CONFIG_VERSION {
            return Err(invalid(format!(
                "Unsupported configuration version {}; expected {CONFIG_VERSION}.",
                self.version
            )));
        }
        let raw_sessions = match (reader, self.sessions) {
            (ConfigReader::Headless, None) => return Err(invalid("sessions must be an array.")),
            (ConfigReader::Window, Some(sessions)) if sessions.len() == 1 => sessions,
            (ConfigReader::Window, _) => {
                return Err(invalid(
                    "The graphical host requires exactly one configured session.",
                ));
            }
            (ConfigReader::Headless, Some(sessions)) => sessions,
        };
        if reader == ConfigReader::Window && raw_sessions[0].is_none() {
            return Err(invalid("The configured session cannot be null."));
        }
        let (paths, content) = match self.process {
            Some(process) => (process.paths, process.content),
            None => (None, None),
        };
        let content = content.map(RawContent::validate).transpose()?;

        let mut sessions = Vec::with_capacity(raw_sessions.len());
        let mut credential_keys = Vec::new();
        for raw in raw_sessions {
            let session = match reader {
                ConfigReader::Headless => {
                    let raw = match raw {
                        Some(raw) if !blank(raw.id.as_deref()) => raw,
                        _ => return Err(invalid("Every session requires a non-empty id.")),
                    };
                    let id = raw.id.clone().unwrap_or_default();
                    if sessions.iter().any(|s: &SessionDescriptor| s.id == id) {
                        return Err(invalid(format!("Duplicate session id '{id}'.")));
                    }
                    let session = raw.validate_headless()?;
                    let key = (
                        session.credential.provider,
                        session.credential.reference.clone(),
                    );
                    if credential_keys.contains(&key) {
                        return Err(invalid(format!(
                            "Credential reference for session '{}' is already in use.",
                            session.id
                        )));
                    }
                    credential_keys.push(key);
                    session
                }
                ConfigReader::Window => {
                    // Checked above: the one session is not null.
                    let Some(raw) = raw else {
                        return Err(invalid("The configured session cannot be null."));
                    };
                    raw.validate_window()?
                }
            };
            sessions.push(session);
        }
        Ok(HostConfig {
            process: ProcessSettings {
                paths: paths.unwrap_or_default(),
                content,
            },
            sessions,
        })
    }
}

impl RawContent {
    fn validate(self) -> Result<ContentDescriptor, ConfigError> {
        if blank(self.dat_directory.as_deref()) || blank(self.prepared_asset_path.as_deref()) {
            return Err(invalid(
                "process.content requires non-empty datDirectory and preparedAssetPath.",
            ));
        }
        let base = self.base_recipe.filter(|&version| version > 0);
        let effective = self.effective_recipe.filter(|&version| version > 0);
        let overlay = self.overlay_path.filter(|path| !path.trim().is_empty());
        let prepared_asset_overlay = match (overlay, base, effective) {
            (Some(path), Some(base_recipe_version), Some(effective_recipe_version)) => {
                Some(PreparedAssetOverlay {
                    path: PathBuf::from(path),
                    base_recipe_version,
                    effective_recipe_version,
                })
            }
            (None, None, None) => None,
            _ => {
                return Err(invalid(
                    "process.content overlay path, base recipe, and effective recipe must be \
                     supplied together.",
                ));
            }
        };
        Ok(ContentDescriptor {
            dat_directory: PathBuf::from(self.dat_directory.unwrap_or_default()),
            prepared_asset_path: PathBuf::from(self.prepared_asset_path.unwrap_or_default()),
            prepared_asset_overlay,
        })
    }
}

impl RawSession {
    fn validate_headless(self) -> Result<SessionDescriptor, ConfigError> {
        let id = self.id.clone().unwrap_or_default();
        let endpoint = validate_endpoint(&id, self.endpoint.as_ref())?;
        let account = validate_account(&id, self.account.as_deref())?;
        let mode = self.headless_mode(&id)?;
        let credential = validate_credential(&id, self.credential.as_ref())?;
        let character_options = validate_character_options(&id, self.character_options)?;
        let plugins = validate_words(&id, "plugins", self.plugins)?;
        let plugin_tags = validate_words(&id, "pluginTags", self.plugin_tags)?;
        let login_commands = validate_login_commands(&id, self.login_commands)?;
        let login_command_delay_ms = validate_delay(&id, self.login_command_delay_ms)?;
        let status_file = validate_status_file(&id, self.status_file)?;
        let plugin_settings = validate_plugin_settings(&id, self.plugin_settings)?;
        Ok(SessionDescriptor {
            id,
            endpoint,
            account,
            mode,
            credential,
            character_options,
            plugins,
            plugin_tags,
            plugin_settings,
            login_commands,
            login_command_delay_ms,
            status_file,
        })
    }

    fn headless_mode(&self, id: &str) -> Result<SessionMode, ConfigError> {
        for (name, is_null) in [
            ("mode", matches!(self.mode, Presence::Null)),
            ("character", matches!(self.character, Presence::Null)),
            ("policy", matches!(self.policy, Presence::Null)),
        ] {
            if is_null {
                return Err(invalid(format!(
                    "Session '{id}' field '{name}' cannot be null; supply a value when allowed \
                     or omit the field."
                )));
            }
        }
        if let Presence::Value(RawMode::Probe) = self.mode {
            if self.character.is_present() {
                return Err(invalid(format!(
                    "Session '{id}' has mode \"probe\" and must omit 'character' — a probe never \
                     selects a character."
                )));
            }
            if self.policy.is_present() {
                return Err(invalid(format!(
                    "Session '{id}' has mode \"probe\" and must omit 'policy' — a probe never \
                     drives a bot policy."
                )));
            }
            return Ok(SessionMode::Probe);
        }
        if self.mode.is_present() {
            return Err(invalid(format!(
                "Session '{id}' is normal play and must omit 'mode'."
            )));
        }
        let Presence::Value(character) = &self.character else {
            return Err(invalid(format!(
                "Session '{id}' requires a character selector."
            )));
        };
        let character = validate_selector(id, character)?;
        let policy = match &self.policy {
            Presence::Value(RawPolicy {
                id: Some(policy_id),
                role,
            }) if !policy_id.trim().is_empty() => PolicyDescriptor {
                id: policy_id.clone(),
                role: *role,
            },
            _ => {
                return Err(invalid(format!(
                    "Session '{id}' requires a non-empty policy id."
                )));
            }
        };
        Ok(SessionMode::Play {
            character: Some(character),
            policy: Some(policy),
        })
    }

    fn validate_window(self) -> Result<SessionDescriptor, ConfigError> {
        if blank(self.id.as_deref()) {
            return Err(invalid("The session requires a non-empty id."));
        }
        let id = self.id.clone().unwrap_or_default();
        let endpoint = validate_endpoint(&id, self.endpoint.as_ref())?;
        let account = validate_account(&id, self.account.as_deref())?;
        let character = match &self.character {
            Presence::Value(character) => Some(validate_selector(&id, character)?),
            Presence::Absent | Presence::Null => None,
        };
        let credential = validate_credential(&id, self.credential.as_ref())?;
        let plugins = validate_words(&id, "plugins", self.plugins)?;
        let plugin_tags = validate_words(&id, "pluginTags", self.plugin_tags)?;
        let plugin_settings = validate_plugin_settings(&id, self.plugin_settings)?;
        let character_options = validate_character_options(&id, self.character_options)?;
        let login_command_delay_ms = validate_delay(&id, self.login_command_delay_ms)?;
        let status_file = validate_status_file(&id, self.status_file)?;
        match self.mode {
            Presence::Value(RawMode::Named(mode)) if mode == "probe" => {
                return Err(invalid(format!(
                    "Session '{id}' has mode 'probe'; probe sessions are headless-only and \
                     cannot run on the graphical host."
                )));
            }
            Presence::Value(RawMode::Named(mode)) => {
                return Err(invalid(format!(
                    "Session '{id}' has unsupported mode '{mode}'."
                )));
            }
            Presence::Value(RawMode::Probe) | Presence::Absent | Presence::Null => {}
        }
        let login_commands = validate_login_commands(&id, self.login_commands)?;
        let policy = self.policy.value().and_then(|policy| {
            policy.id.map(|id| PolicyDescriptor {
                id,
                role: policy.role,
            })
        });
        Ok(SessionDescriptor {
            id,
            endpoint,
            account,
            mode: SessionMode::Play { character, policy },
            credential,
            character_options,
            plugins,
            plugin_tags,
            plugin_settings,
            login_commands,
            login_command_delay_ms,
            status_file,
        })
    }
}

fn validate_endpoint(id: &str, endpoint: Option<&RawEndpoint>) -> Result<Endpoint, ConfigError> {
    match endpoint {
        Some(RawEndpoint {
            host: Some(host),
            port,
        }) if !host.trim().is_empty() && (1..=65535).contains(port) => Ok(Endpoint {
            host: host.clone(),
            port: u16::try_from(*port).unwrap_or_default(),
        }),
        _ => Err(invalid(format!(
            "Session '{id}' requires a host and a port from 1 through 65535."
        ))),
    }
}

fn validate_account(id: &str, account: Option<&str>) -> Result<String, ConfigError> {
    match account {
        Some(account) if !account.trim().is_empty() => Ok(account.to_owned()),
        _ => Err(invalid(format!(
            "Session '{id}' requires a non-empty account."
        ))),
    }
}

fn validate_selector(id: &str, raw: &RawCharacter) -> Result<CharacterSelector, ConfigError> {
    let name = raw.name.as_deref().filter(|name| !name.trim().is_empty());
    let selector = match (raw.index, raw.id, name) {
        (Some(index), None, None) => u32::try_from(index).ok().map(CharacterSelector::Index),
        (None, Some(character_id), None) if character_id != 0 => {
            Some(CharacterSelector::Id(character_id))
        }
        (None, None, Some(name)) => Some(CharacterSelector::Name(name.to_owned())),
        _ => None,
    };
    selector.ok_or_else(|| {
        invalid(format!(
            "Session '{id}' character selector must specify exactly one valid index, id, or name."
        ))
    })
}

fn validate_credential(
    id: &str,
    credential: Option<&(CredentialProvider, Option<String>)>,
) -> Result<CredentialReference, ConfigError> {
    match credential {
        Some((provider, Some(reference))) if !reference.trim().is_empty() => {
            Ok(CredentialReference {
                provider: *provider,
                reference: reference.clone(),
            })
        }
        _ => Err(invalid(format!(
            "Session '{id}' requires a credential reference."
        ))),
    }
}

fn validate_character_options(
    id: &str,
    declared: Option<Vec<(String, bool)>>,
) -> Result<Option<BTreeMap<CharacterOption, bool>>, ConfigError> {
    let Some(declared) = declared else {
        return Ok(None);
    };
    if let Some(complaint) = character_options::describe(id, &declared) {
        return Err(invalid(complaint));
    }
    Ok(Some(
        declared
            .into_iter()
            .filter_map(|(name, value)| CharacterOption::from_name(&name).map(|o| (o, value)))
            .collect(),
    ))
}

fn validate_words(
    id: &str,
    member: &str,
    words: Option<Vec<Option<String>>>,
) -> Result<Option<Vec<String>>, ConfigError> {
    let Some(words) = words else {
        return Ok(None);
    };
    words
        .into_iter()
        .map(|word| match word {
            Some(word) if !word.trim().is_empty() => Ok(word),
            _ => Err(invalid(format!(
                "Session '{id}' {member} entries must be non-empty strings."
            ))),
        })
        .collect::<Result<Vec<_>, _>>()
        .map(Some)
}

fn validate_login_commands(
    id: &str,
    commands: Option<Vec<Option<String>>>,
) -> Result<Option<Vec<String>>, ConfigError> {
    let Some(commands) = commands else {
        return Ok(None);
    };
    commands
        .into_iter()
        .map(|command| {
            command.ok_or_else(|| {
                invalid(format!(
                    "Session '{id}' loginCommands entries cannot be null."
                ))
            })
        })
        .collect::<Result<Vec<_>, _>>()
        .map(Some)
}

fn validate_delay(id: &str, delay: i32) -> Result<u32, ConfigError> {
    u32::try_from(delay).map_err(|_| {
        invalid(format!(
            "Session '{id}' loginCommandDelayMs must be non-negative."
        ))
    })
}

fn validate_status_file(
    id: &str,
    status_file: Option<String>,
) -> Result<Option<PathBuf>, ConfigError> {
    match status_file {
        Some(path) if path.trim().is_empty() => Err(invalid(format!(
            "Session '{id}' statusFile must be a non-empty path when present."
        ))),
        other => Ok(other.map(PathBuf::from)),
    }
}

/// What is wrong with a declared plugin-settings map, in the words both hosts
/// use, or `None` when nothing is.
fn describe_plugin_settings_fault(declared: &RawPluginSettings) -> Option<String> {
    for (plugin_id, settings) in declared {
        let Some(settings) = settings else {
            return Some(format!("pluginSettings['{plugin_id}'] cannot be null."));
        };
        for (key, value) in settings {
            if value.is_none() {
                return Some(format!(
                    "pluginSettings['{plugin_id}']['{key}'] cannot be null."
                ));
            }
        }
    }
    None
}

#[allow(clippy::type_complexity)]
fn validate_plugin_settings(
    id: &str,
    declared: Option<RawPluginSettings>,
) -> Result<Option<BTreeMap<String, BTreeMap<String, String>>>, ConfigError> {
    let Some(declared) = declared else {
        return Ok(None);
    };
    if let Some(fault) = describe_plugin_settings_fault(&declared) {
        return Err(invalid(format!("Session '{id}' {fault}")));
    }
    Ok(Some(
        declared
            .into_iter()
            .map(|(plugin_id, settings)| {
                let settings = settings
                    .unwrap_or_default()
                    .into_iter()
                    .map(|(key, value)| (key, value.unwrap_or_default()))
                    .collect();
                (plugin_id, settings)
            })
            .collect(),
    ))
}
