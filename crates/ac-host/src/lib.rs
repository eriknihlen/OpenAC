//! Host configuration: everything a process decides before its first session
//! connects.
//!
//! - [`parse_command_line`]: the command grammar (`--help`, `validate`, `run`).
//! - [`load_config`] / [`load_config_file`]: the versioned session-config
//!   document, checked strictly, into a typed [`HostConfig`].
//! - [`PathSet::resolve`]: the install root and the folders beneath it.
//! - [`CredentialResolver`] / [`resolve_session_logins`]: each session's
//!   password, held as a redacted [`Secret`].
//! - [`StatusWriter`]: a session's status file, one JSON line per event.
//! - [`ExitCode`]: the process's exit codes.
//! - [`local_time::LocalZone`]: the player's local time zone, for the times
//!   the client and the launcher show.
//! - [`process_stats`]: the process's working set, handles and CPU use, for
//!   plugins that report them.
//!
//! [`start`] strings the first steps together the way a host's `main` does.
//! Nothing here opens a socket; file reads happen only where named.

pub mod character_options;
pub mod command;
pub mod config;
pub mod credentials;
pub mod exit;
pub mod install_lease;
pub mod json;
pub mod local_time;
pub mod paths;
pub mod process_stats;
pub mod settings;
pub mod status;

pub use character_options::CharacterOption;
pub use command::{Command, CommandLine, ConsoleStream, HELP, UsageError, parse_command_line};
pub use config::{
    CONFIG_VERSION, CharacterSelector, ConfigError, ConfigReader, ContentDescriptor,
    CredentialProvider, CredentialReference, Endpoint, HostConfig, PolicyDescriptor, PolicyRole,
    PreparedAssetOverlay, ProcessSettings, SessionDescriptor, SessionMode, load_config,
    load_config_file,
};
pub use credentials::{
    CredentialError, CredentialResolver, DirectCredentials, Secret, SessionLogin,
    resolve_session_logins,
};
pub use exit::ExitCode;
pub use install_lease::InstallSessionLease;
pub use paths::{KnownFolder, Os, PathOverrides, PathSet, Platform, RootSource, SystemPlatform};
pub use status::{
    FileSink, HEARTBEAT_INTERVAL, RosterEntry, RosterReport, ServerRefusalNote, StatusSink,
    StatusWriter, WriterSink,
};

use std::path::Path;

/// Why startup stopped, sorted by exit code.
#[derive(Debug, thiserror::Error)]
pub enum HostError {
    /// The command line does not fit the grammar.
    #[error(transparent)]
    Usage(#[from] UsageError),
    /// The document, the directories or a startup option are unusable.
    #[error(transparent)]
    Config(#[from] ConfigError),
    /// A password could not be had.
    #[error(transparent)]
    Credential(#[from] CredentialError),
}

impl HostError {
    /// The exit code this error ends the process with.
    pub const fn exit_code(&self) -> ExitCode {
        match self {
            Self::Usage(_) => ExitCode::Usage,
            Self::Config(_) => ExitCode::Configuration,
            Self::Credential(_) => ExitCode::Credential,
        }
    }

    /// The one line to print on standard error. A usage error never echoes
    /// the command line, which may hold a password.
    pub fn report_line(&self) -> String {
        match self {
            Self::Usage(_) => "Invalid command. Run --help for usage.".to_owned(),
            Self::Config(error) => format!("Configuration invalid: {error}"),
            Self::Credential(error) => format!("Credential unavailable: {error}"),
        }
    }
}

/// What startup decided.
#[derive(Debug)]
pub enum Startup {
    /// Print [`HELP`] on standard output; exit [`ExitCode::Success`].
    Help,
    /// The document is valid; print `summary` on standard output and exit
    /// [`ExitCode::Success`].
    Validated {
        /// `Configuration valid: version 1, N session(s).`
        summary: String,
        /// The checked document.
        config: HostConfig,
        /// The resolved directories.
        paths: PathSet,
    },
    /// Run the sessions.
    Run(RunPlan),
}

/// Everything a host needs to start its sessions.
#[derive(Debug)]
pub struct RunPlan {
    /// The checked document (at least one session).
    pub config: HostConfig,
    /// The resolved directories; credential files resolve against
    /// `paths.config_directory`.
    pub paths: PathSet,
    /// The command line's account and password (only with one session).
    pub direct_credentials: Option<DirectCredentials>,
    /// Whether the console runs.
    pub console: bool,
    /// Where the console prints.
    pub console_stream: ConsoleStream,
}

/// Parses the command line, loads and checks the document, and resolves the
/// directories, in that order. Credentials are resolved later, by the host,
/// with [`resolve_session_logins`].
pub fn start(
    arguments: &[String],
    platform: &dyn Platform,
    standard_input_is_terminal: bool,
) -> Result<Startup, HostError> {
    let (is_run, command_line) = match parse_command_line(arguments)? {
        Command::Help => return Ok(Startup::Help),
        Command::Validate(command_line) => (false, command_line),
        Command::Run(command_line) => (true, command_line),
    };
    let config = load_config_file(Path::new(&command_line.config_path), ConfigReader::Headless)?;
    let paths = PathSet::resolve(&config.process.paths.merge(&command_line.paths), platform)?;
    if !is_run {
        let summary = format!(
            "Configuration valid: version {}, {} session(s).",
            config.version(),
            config.sessions.len()
        );
        return Ok(Startup::Validated {
            summary,
            config,
            paths,
        });
    }
    let console = command::resolve_console(
        command_line.console,
        platform.var(command::CONSOLE_VAR).as_deref(),
        standard_input_is_terminal,
    );
    let console_stream = command::resolve_console_stream(
        command_line.console_stream,
        platform.var(command::CONSOLE_STREAM_VAR).as_deref(),
    )?;
    if config.sessions.is_empty() {
        return Err(ConfigError::Invalid(
            "Headless run mode requires at least one session.".into(),
        )
        .into());
    }
    if command_line.direct_credentials.is_some() && config.sessions.len() != 1 {
        return Err(ConfigError::Invalid(
            "Direct credentials require exactly one configured session.".into(),
        )
        .into());
    }
    Ok(Startup::Run(RunPlan {
        config,
        paths,
        direct_credentials: command_line.direct_credentials,
        console,
        console_stream,
    }))
}
