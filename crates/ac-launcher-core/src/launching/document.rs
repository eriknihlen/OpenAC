//! Composing the session-config document a launched client reads: one
//! session, the password through standard input only, an explicit plugin
//! allow-list (blank loads nothing; refused, duplicated and blocked plugins
//! removed), written in the session's own folder,
//! `logs/<server>/<character>/<local start time>/` (the account's name
//! for a session started without a character), beside its status file and
//! the client's log.
//!
//! The document is `ac-host`'s own `HostConfig`, so whatever the launcher
//! writes is exactly what the host's reader accepts.

use std::path::{Path, PathBuf};

use ac_host::{
    CharacterSelector, ContentDescriptor, CredentialProvider, CredentialReference, Endpoint,
    HostConfig, PathSet, PolicyDescriptor, PolicyRole, PreparedAssetOverlay, ProcessSettings,
    SessionDescriptor, SessionMode,
};

use crate::installation::LauncherInstallRecord;
use crate::json::{self, ObjectBuilder, Out};
use crate::plugins::{InstalledPluginRecordStore, PluginCatalog, PluginInventory};
use crate::profiles::{AccountProfile, CharacterProfile, LaunchMode, ServerProfile, character_id};
use crate::updates::LauncherVersion;

/// The credential reference the launcher's documents use.
pub const STANDARD_INPUT_REFERENCE: &str = "session";

/// A composed document and where its files go.
#[derive(Debug, Clone, PartialEq)]
pub struct ComposedSessionConfig {
    /// The session id.
    pub session_id: String,
    /// The session's folder (see [`ac_host::layout::create_session_folder`]).
    pub session_directory: PathBuf,
    /// `<session folder>/session.json`.
    pub config_file_path: PathBuf,
    /// `<session folder>/status.jsonl`.
    pub status_file_path: PathBuf,
    /// `<session folder>/client.log`: the client's standard output and
    /// error.
    pub client_log_path: PathBuf,
    /// The document.
    pub document: HostConfig,
    /// One line per configured plugin left out (refused, duplicated,
    /// blocked).
    pub plugin_status_lines: Vec<String>,
}

/// Why a document could not be composed or written.
#[derive(Debug, thiserror::Error)]
pub enum ComposeError {
    /// The content was prepared for a client not yet confirmed.
    #[error(
        "Prepared content cannot be launched until the matching client has been confirmed or \
         installed."
    )]
    ClientCompatibilityUnconfirmed,
    /// The character has neither a usable id nor a name.
    #[error("Character has neither a usable id nor a name to select by.")]
    NoSelector,
    /// A required argument was blank.
    #[error("The session id cannot be empty or white space.")]
    BlankSessionId,
    /// The server's port is out of range.
    #[error("Port {0} is outside the valid 1-65535 range.")]
    InvalidPort(i32),
    /// Writing the document failed.
    #[error("{0}")]
    Io(#[from] std::io::Error),
}

/// Composes and writes session documents (a fake in orchestrator tests).
pub trait SessionConfigService: Send + Sync {
    /// Composes a play session and writes it.
    #[allow(clippy::too_many_arguments)]
    fn compose_and_write(
        &self,
        server: &ServerProfile,
        account: &AccountProfile,
        character: &CharacterProfile,
        install: &LauncherInstallRecord,
        paths: &PathSet,
        session_id: &str,
        login_command_delay_ms: Option<u32>,
        catalog: Option<&PluginCatalog>,
    ) -> Result<ComposedSessionConfig, ComposeError>;

    /// Composes a probe session and writes it.
    fn compose_probe_and_write(
        &self,
        server: &ServerProfile,
        account: &AccountProfile,
        install: &LauncherInstallRecord,
        paths: &PathSet,
        session_id: &str,
    ) -> Result<ComposedSessionConfig, ComposeError>;
}

/// The real composer.
#[derive(Debug, Clone, Copy, Default)]
pub struct LauncherSessionConfigService;

impl SessionConfigService for LauncherSessionConfigService {
    fn compose_and_write(
        &self,
        server: &ServerProfile,
        account: &AccountProfile,
        character: &CharacterProfile,
        install: &LauncherInstallRecord,
        paths: &PathSet,
        session_id: &str,
        login_command_delay_ms: Option<u32>,
        catalog: Option<&PluginCatalog>,
    ) -> Result<ComposedSessionConfig, ComposeError> {
        write(compose(
            server,
            account,
            character,
            install,
            paths,
            session_id,
            login_command_delay_ms,
            catalog,
        )?)
    }

    fn compose_probe_and_write(
        &self,
        server: &ServerProfile,
        account: &AccountProfile,
        install: &LauncherInstallRecord,
        paths: &PathSet,
        session_id: &str,
    ) -> Result<ComposedSessionConfig, ComposeError> {
        write(compose_probe(server, account, install, paths, session_id)?)
    }
}

/// The files of the session folder `directory`: the document, the status
/// file and the client log.
fn session_paths(directory: &Path) -> (PathBuf, PathBuf, PathBuf) {
    use ac_host::paths::{
        SESSION_CLIENT_LOG_FILE_NAME, SESSION_CONFIG_FILE_NAME, SESSION_STATUS_FILE_NAME,
    };
    (
        directory.join(SESSION_CONFIG_FILE_NAME),
        directory.join(SESSION_STATUS_FILE_NAME),
        directory.join(SESSION_CLIENT_LOG_FILE_NAME),
    )
}

/// The folder a session starting now is given, before [`write`] makes it
/// (a later number when one of the same second is already there):
/// `logs/<server>/<character>/<local start time>`.
fn session_directory(paths: &PathSet, server: &ServerProfile, filed_under: &str) -> PathBuf {
    let now = std::time::SystemTime::now();
    let now_ms = now
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |since| {
            i64::try_from(since.as_millis()).unwrap_or(i64::MAX)
        });
    let offset = ac_host::local_time::LocalZone::system().utc_offset_seconds(now_ms);
    paths
        .session_logs_directory(&server.name, filed_under)
        .join(ac_host::layout::session_folder_name(
            now_ms + i64::from(offset) * 1000,
        ))
}

/// The name a session's folder is filed under: the character's, or the
/// account's when the player picks the character in the client (or the
/// character has no name to file it under).
fn filed_under(account: &AccountProfile, character: Option<&CharacterProfile>) -> String {
    match character {
        Some(character)
            if character.launch_mode != LaunchMode::GuiSelect
                && !character.name.trim().is_empty() =>
        {
            character.name.clone()
        }
        _ => account.account.clone(),
    }
}

fn content(install: &LauncherInstallRecord) -> ContentDescriptor {
    ContentDescriptor {
        dat_directory: install.dat_directory.clone(),
        prepared_asset_path: install.prepared_asset_path.clone(),
        prepared_asset_overlay: install.prepared_asset_overlay_path.as_ref().map(|path| {
            PreparedAssetOverlay {
                path: path.clone(),
                base_recipe_version: install.bake_tool_version,
                effective_recipe_version: install.resolved_bake_tool_version(),
            }
        }),
    }
}

fn endpoint(server: &ServerProfile) -> Result<Endpoint, ComposeError> {
    Ok(Endpoint {
        host: server.host.clone(),
        port: u16::try_from(server.port)
            .ok()
            .filter(|&port| port != 0)
            .ok_or(ComposeError::InvalidPort(server.port))?,
    })
}

fn credential() -> CredentialReference {
    CredentialReference {
        provider: CredentialProvider::StandardInput,
        reference: STANDARD_INPUT_REFERENCE.to_owned(),
    }
}

fn selector(character: &CharacterProfile) -> Result<CharacterSelector, ComposeError> {
    match character_id::try_parse(character.id.as_deref()) {
        Some(id) if id != 0 => Ok(CharacterSelector::Id(id)),
        _ if !character.name.trim().is_empty() => {
            Ok(CharacterSelector::Name(character.name.clone()))
        }
        _ => Err(ComposeError::NoSelector),
    }
}

/// Composes a play session for `character` in its launch mode: a character
/// selector unless the player picks at the character screen, the idle bot
/// policy only for a windowless session.
#[allow(clippy::too_many_arguments)]
pub fn compose(
    server: &ServerProfile,
    account: &AccountProfile,
    character: &CharacterProfile,
    install: &LauncherInstallRecord,
    paths: &PathSet,
    session_id: &str,
    login_command_delay_ms: Option<u32>,
    catalog: Option<&PluginCatalog>,
) -> Result<ComposedSessionConfig, ComposeError> {
    if session_id.trim().is_empty() {
        return Err(ComposeError::BlankSessionId);
    }
    if install.requires_client_compatibility_confirmation {
        return Err(ComposeError::ClientCompatibilityUnconfirmed);
    }
    let session_directory =
        session_directory(paths, server, &filed_under(account, Some(character)));
    let (config_file_path, status_file_path, client_log_path) = session_paths(&session_directory);
    let selector = match character.launch_mode {
        LaunchMode::GuiSelect => None,
        _ => Some(selector(character)?),
    };
    let policy = (character.launch_mode == LaunchMode::Headless).then(|| PolicyDescriptor {
        id: "idle".into(),
        role: None,
    });
    let (plugins, plugin_status_lines) =
        compose_plugin_allow_list(account.plugins_for(Some(character)), catalog, paths);
    let session = SessionDescriptor {
        id: session_id.to_owned(),
        endpoint: endpoint(server)?,
        account: account.account.clone(),
        mode: SessionMode::Play {
            character: selector,
            policy,
        },
        credential: credential(),
        character_options: None,
        plugins: Some(plugins),
        plugin_tags: None,
        plugin_settings: None,
        login_commands: (!account.login_commands.is_empty())
            .then(|| account.login_commands.clone()),
        login_command_delay_ms: login_command_delay_ms
            .unwrap_or(ac_host::config::DEFAULT_LOGIN_COMMAND_DELAY_MS),
        status_file: Some(status_file_path.clone()),
    };
    Ok(ComposedSessionConfig {
        session_id: session_id.to_owned(),
        session_directory,
        config_file_path,
        status_file_path,
        client_log_path,
        document: HostConfig {
            process: ProcessSettings {
                paths: ac_host::PathOverrides::default(),
                content: Some(content(install)),
            },
            sessions: vec![session],
        },
        plugin_status_lines,
    })
}

/// Composes a probe: connect, read the character list, never pick a
/// character, load no plugins.
pub fn compose_probe(
    server: &ServerProfile,
    account: &AccountProfile,
    install: &LauncherInstallRecord,
    paths: &PathSet,
    session_id: &str,
) -> Result<ComposedSessionConfig, ComposeError> {
    if session_id.trim().is_empty() {
        return Err(ComposeError::BlankSessionId);
    }
    if install.requires_client_compatibility_confirmation {
        return Err(ComposeError::ClientCompatibilityUnconfirmed);
    }
    let session_directory = session_directory(paths, server, &filed_under(account, None));
    let (config_file_path, status_file_path, client_log_path) = session_paths(&session_directory);
    let session = SessionDescriptor {
        id: session_id.to_owned(),
        endpoint: endpoint(server)?,
        account: account.account.clone(),
        mode: SessionMode::Probe,
        credential: credential(),
        character_options: None,
        plugins: Some(Vec::new()),
        plugin_tags: None,
        plugin_settings: None,
        login_commands: None,
        login_command_delay_ms: ac_host::config::DEFAULT_LOGIN_COMMAND_DELAY_MS,
        status_file: Some(status_file_path.clone()),
    };
    Ok(ComposedSessionConfig {
        session_id: session_id.to_owned(),
        session_directory,
        config_file_path,
        status_file_path,
        client_log_path,
        document: HostConfig {
            process: ProcessSettings {
                paths: ac_host::PathOverrides::default(),
                content: Some(content(install)),
            },
            sessions: vec![session],
        },
        plugin_status_lines: Vec::new(),
    })
}

/// Makes the session folder and writes the document into it. A folder of
/// that name already there (a session of the same character started in
/// the same second) is never reused: the session gets the next free name
/// (` (2)`, ` (3)`, ...), and the document's paths follow it.
pub fn write(mut composed: ComposedSessionConfig) -> Result<ComposedSessionConfig, ComposeError> {
    let claimed = ac_host::layout::claim_session_folder(&composed.session_directory)?;
    if claimed != composed.session_directory {
        let (config_file_path, status_file_path, client_log_path) = session_paths(&claimed);
        for session in &mut composed.document.sessions {
            if session.status_file.as_deref() == Some(composed.status_file_path.as_path()) {
                session.status_file = Some(status_file_path.clone());
            }
        }
        composed.session_directory = claimed;
        composed.config_file_path = config_file_path;
        composed.status_file_path = status_file_path;
        composed.client_log_path = client_log_path;
    }
    std::fs::write(&composed.config_file_path, serialize(&composed.document))?;
    Ok(composed)
}

/// The blank-means-none allow-list: an empty list or `none` loads nothing;
/// a direct install that fails its checks or an id installed twice is left
/// out (the client applies neither check itself); a blocked id is left out
/// when a list (given, or cached at `<cache>/plugins.json`) blocks the
/// installed version.
fn compose_plugin_allow_list(
    configured: &[String],
    catalog: Option<&PluginCatalog>,
    paths: &PathSet,
) -> (Vec<String>, Vec<String>) {
    if configured.is_empty()
        || (configured.len() == 1 && configured[0].eq_ignore_ascii_case("none"))
    {
        return (Vec::new(), Vec::new());
    }
    let mut status_lines = Vec::new();
    let mut records = InstalledPluginRecordStore::for_paths(paths);
    // A corrupt record file only costs this pass its memory of managed
    // plugins; every folder is then checked as a direct install.
    if records.load().is_err() {
        records.records.clear();
    }
    let inventory = PluginInventory::new(paths).build(&records, None, None);
    let find = |id: &str| {
        inventory
            .iter()
            .find(|info| info.id.eq_ignore_ascii_case(id))
    };
    let mut checked = Vec::with_capacity(configured.len());
    for id in configured {
        if let Some(info) = find(id) {
            if info.refusal.is_some() {
                status_lines.push(format!(
                    "Plugin '{id}' failed its install checks and was not loaded."
                ));
                continue;
            }
            if info.has_duplicate {
                status_lines.push(format!(
                    "Plugin '{id}' has more than one copy installed and was not loaded."
                ));
                continue;
            }
        }
        checked.push(id.clone());
    }
    let cached;
    let effective = match catalog {
        Some(catalog) => Some(catalog),
        None => {
            cached = try_load_cached_catalog(paths);
            cached.as_ref()
        }
    };
    let Some(effective) = effective else {
        return (checked, status_lines);
    };
    let mut allowed = Vec::with_capacity(checked.len());
    for id in checked {
        // A block can name single versions, so it is judged against the
        // installed one; an id with no readable version takes any block.
        let installed = find(&id).and_then(|info| LauncherVersion::try_parse(&info.version));
        if effective.is_blocked(&id, installed.as_ref()) {
            status_lines.push(format!("Plugin '{id}' is blocked and was not loaded."));
            continue;
        }
        allowed.push(id);
    }
    (allowed, status_lines)
}

fn try_load_cached_catalog(paths: &PathSet) -> Option<PluginCatalog> {
    let text = std::fs::read_to_string(paths.cache_directory.join("plugins.json")).ok()?;
    PluginCatalog::parse(&text).ok()
}

fn path_text(path: &Path) -> Out {
    Out::str(path.to_string_lossy())
}

/// The document as indented JSON: members ac-host's reader knows, absent
/// ones left out (and a login-command delay equal to the default left out,
/// as OpenAC leaves an unset delay out).
pub fn serialize(document: &HostConfig) -> String {
    let mut process = ObjectBuilder::new();
    let overrides = &document.process.paths;
    if overrides.any() {
        process = process.member(
            "paths",
            ObjectBuilder::new()
                .optional(
                    "rootDirectory",
                    overrides.root_directory.clone().map(Out::Str),
                )
                .optional(
                    "configDirectory",
                    overrides.config_directory.clone().map(Out::Str),
                )
                .optional(
                    "dataDirectory",
                    overrides.data_directory.clone().map(Out::Str),
                )
                .optional(
                    "cacheDirectory",
                    overrides.cache_directory.clone().map(Out::Str),
                )
                .build(),
        );
    }
    if let Some(content) = &document.process.content {
        let overlay = content.prepared_asset_overlay.as_ref();
        process = process.member(
            "content",
            ObjectBuilder::new()
                .member("datDirectory", path_text(&content.dat_directory))
                .member("preparedAssetPath", path_text(&content.prepared_asset_path))
                .optional(
                    "preparedAssetOverlayPath",
                    overlay.map(|o| path_text(&o.path)),
                )
                .optional(
                    "preparedAssetBaseRecipeVersion",
                    overlay.map(|o| Out::Int(i64::from(o.base_recipe_version))),
                )
                .optional(
                    "preparedAssetEffectiveRecipeVersion",
                    overlay.map(|o| Out::Int(i64::from(o.effective_recipe_version))),
                )
                .build(),
        );
    }
    let sessions = document.sessions.iter().map(session_json).collect();
    json::to_pretty(
        &ObjectBuilder::new()
            .member("version", Out::Int(i64::from(document.version())))
            .member("process", process.build())
            .member("sessions", Out::Array(sessions))
            .build(),
    )
}

fn session_json(session: &SessionDescriptor) -> Out {
    let (mode, character, policy) = match &session.mode {
        SessionMode::Probe => (Some(Out::str("probe")), None, None),
        SessionMode::Play { character, policy } => (None, character.as_ref(), policy.as_ref()),
    };
    let character = character.map(|selector| match selector {
        CharacterSelector::Index(index) => ObjectBuilder::new()
            .member("index", Out::Int(i64::from(*index)))
            .build(),
        CharacterSelector::Id(id) => ObjectBuilder::new()
            .member("id", Out::Int(i64::from(*id)))
            .build(),
        CharacterSelector::Name(name) => {
            ObjectBuilder::new().member("name", Out::str(name)).build()
        }
    });
    let policy = policy.map(|policy| {
        ObjectBuilder::new()
            .member("id", Out::str(&policy.id))
            .optional(
                "role",
                policy.role.map(|role| {
                    Out::str(match role {
                        PolicyRole::Leader => "leader",
                        PolicyRole::Recruit => "recruit",
                    })
                }),
            )
            .build()
    });
    let provider = match session.credential.provider {
        CredentialProvider::Environment => "environment",
        CredentialProvider::StandardInput => "standardInput",
        CredentialProvider::File => "file",
    };
    ObjectBuilder::new()
        .member("id", Out::str(&session.id))
        .optional("mode", mode)
        .member(
            "endpoint",
            ObjectBuilder::new()
                .member("host", Out::str(&session.endpoint.host))
                .member("port", Out::Int(i64::from(session.endpoint.port)))
                .build(),
        )
        .member("account", Out::str(&session.account))
        .optional("character", character)
        .optional("policy", policy)
        .member(
            "credential",
            ObjectBuilder::new()
                .member("provider", Out::str(provider))
                .member("reference", Out::str(&session.credential.reference))
                .build(),
        )
        .optional("plugins", session.plugins.clone().map(Out::strings))
        .optional("pluginTags", session.plugin_tags.clone().map(Out::strings))
        .optional(
            "pluginSettings",
            session.plugin_settings.as_ref().map(|settings| {
                Out::Object(
                    settings
                        .iter()
                        .map(|(plugin, values)| {
                            (
                                plugin.clone(),
                                Out::Object(
                                    values
                                        .iter()
                                        .map(|(name, value)| (name.clone(), Out::str(value)))
                                        .collect(),
                                ),
                            )
                        })
                        .collect(),
                )
            }),
        )
        .optional(
            "characterOptions",
            session.character_options.as_ref().map(|options| {
                Out::Object(
                    options
                        .iter()
                        .map(|(option, value)| (option.name().to_owned(), Out::Bool(*value)))
                        .collect(),
                )
            }),
        )
        .optional(
            "loginCommands",
            session.login_commands.clone().map(Out::strings),
        )
        .optional(
            "loginCommandDelayMs",
            (session.login_command_delay_ms != ac_host::config::DEFAULT_LOGIN_COMMAND_DELAY_MS)
                .then(|| Out::Int(i64::from(session.login_command_delay_ms))),
        )
        .optional("statusFile", session.status_file.as_deref().map(path_text))
        .build()
}
