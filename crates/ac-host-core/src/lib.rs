//! What the client and the launcher share about a process, in one crate the
//! launcher links without the client.
//!
//! - [`load_config`] / [`load_config_file`]: the versioned session-config
//!   document, checked strictly, into a typed [`HostConfig`]. The launcher
//!   writes it, the client reads it.
//! - [`PathSet::resolve`]: the install root and the folders beneath it;
//!   [`layout`]: the session and log folders under it.
//! - [`StatusWriter`]: a session's status file, one JSON line per event.
//! - [`json`]: the strict JSON reader and writer both documents use.
//! - [`local_time::LocalZone`]: the player's local time zone, for the times
//!   the client and the launcher show.
//! - [`ExitCode`]: the client's exit codes.
//! - [`console_kind`]: the kind marks on console lines a launcher asks for.
//! - [`InstallSessionLease`]: the lock that keeps an update from replacing
//!   files a running session uses.
//!
//! `ac-host` re-exports all of it, so the client names these items as before
//! and the launcher and the client share one implementation.

pub mod character_options;
pub mod config;
pub mod console_kind;
pub mod exit;
pub mod install_lease;
pub mod json;
pub mod layout;
pub mod local_time;
pub mod paths;
pub mod status;

pub use character_options::CharacterOption;
pub use config::{
    CONFIG_VERSION, CharacterSelector, ConfigError, ConfigReader, ContentDescriptor,
    CredentialProvider, CredentialReference, Endpoint, HostConfig, PolicyDescriptor, PolicyRole,
    PreparedAssetOverlay, ProcessSettings, RemoteDescriptor, SessionDescriptor, SessionMode,
    load_config, load_config_file,
};
pub use exit::ExitCode;
pub use install_lease::InstallSessionLease;
pub use paths::{KnownFolder, Os, PathOverrides, PathSet, Platform, RootSource, SystemPlatform};
pub use status::{
    FileSink, HEARTBEAT_INTERVAL, HEARTBEAT_VARIABLE, RosterEntry, RosterReport, ServerRefusalNote,
    StatusSink, StatusWriter, WriterSink, heartbeat_requested,
};
