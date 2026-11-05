//! Passwords: where they come from and how they are held.
//!
//! A session's document names a provider and a reference, never the password
//! itself. The providers are an environment variable, one line of standard
//! input, or a file readable only by its owner. A resolved password is a
//! [`Secret`]: it prints as `[redacted:<reference>]` and its memory is
//! overwritten when it is dropped.

use std::fmt;
use std::io::{self, BufRead};
use std::path::{Path, PathBuf};

use crate::config::{CredentialProvider, CredentialReference, HostConfig, SessionDescriptor};
use crate::paths::{Platform, full_path};

/// Reference id given to a password from the command line.
pub const COMMAND_LINE_REFERENCE: &str = "command-line";

/// Why a password could not be had. The message never contains a secret.
#[derive(Debug, thiserror::Error)]
#[error("{message}")]
pub struct CredentialError {
    message: String,
    #[source]
    source: Option<io::Error>,
}

impl CredentialError {
    fn new(message: String) -> Self {
        Self {
            message,
            source: None,
        }
    }
}

/// A password. Never printed; wiped on drop.
pub struct Secret {
    reference_id: String,
    value: String,
}

impl Secret {
    /// Wraps `value`. An empty value is refused.
    pub fn new(reference_id: impl Into<String>, value: String) -> Result<Self, CredentialError> {
        let reference_id = reference_id.into();
        if value.is_empty() {
            return Err(CredentialError::new(format!(
                "Credential '{reference_id}' resolved to an empty secret."
            )));
        }
        Ok(Self {
            reference_id,
            value,
        })
    }

    /// Which reference produced this secret.
    pub fn reference_id(&self) -> &str {
        &self.reference_id
    }

    /// The password itself, for the one place that sends it.
    pub fn reveal(&self) -> &str {
        &self.value
    }
}

impl Drop for Secret {
    fn drop(&mut self) {
        // Best effort: overwrite the bytes before the allocation is freed.
        let mut bytes = std::mem::take(&mut self.value).into_bytes();
        bytes.fill(0);
        std::hint::black_box(&bytes);
    }
}

impl fmt::Debug for Secret {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "[redacted:{}]", self.reference_id)
    }
}

impl fmt::Display for Secret {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "[redacted:{}]", self.reference_id)
    }
}

/// Account and password given on the command line for a one-session run.
#[derive(Debug)]
pub struct DirectCredentials {
    /// Replaces the session's `account`.
    pub user: String,
    /// The password.
    pub password: Secret,
}

/// Resolves credential references against the process's sources.
pub struct CredentialResolver<'a> {
    standard_input: &'a mut dyn BufRead,
    base_directory: PathBuf,
    platform: &'a dyn Platform,
}

impl fmt::Debug for CredentialResolver<'_> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("CredentialResolver")
            .field("base_directory", &self.base_directory)
            .finish_non_exhaustive()
    }
}

impl<'a> CredentialResolver<'a> {
    /// A resolver reading `standard_input` for standard-input references and
    /// resolving file references against `base_directory` (the config
    /// directory).
    pub fn new(
        standard_input: &'a mut dyn BufRead,
        base_directory: &Path,
        platform: &'a dyn Platform,
    ) -> Self {
        let current = platform.current_dir().unwrap_or_default();
        Self {
            standard_input,
            base_directory: full_path(base_directory, &current),
            platform,
        }
    }

    /// The password for `session_id`.
    pub fn resolve(
        &mut self,
        session_id: &str,
        credential: &CredentialReference,
    ) -> Result<Secret, CredentialError> {
        let reference = credential.reference.as_str();
        let value = match credential.provider {
            CredentialProvider::Environment => self.environment(reference),
            CredentialProvider::StandardInput => self.standard_input(reference),
            CredentialProvider::File => self.file(reference),
        };
        let value = value.map_err(|failure| match failure {
            Failure::Credential(error) => error,
            Failure::Io(source) => CredentialError {
                message: format!(
                    "Credential '{reference}' for session '{session_id}' could not be resolved."
                ),
                source: Some(source),
            },
        })?;
        Secret::new(reference, value)
    }

    fn environment(&self, reference: &str) -> Result<String, Failure> {
        match self.platform.var(reference) {
            Some(value) if !value.is_empty() => Ok(value),
            _ => Err(Failure::credential(format!(
                "Credential environment reference '{reference}' is unavailable."
            ))),
        }
    }

    fn standard_input(&mut self, reference: &str) -> Result<String, Failure> {
        let mut line = String::new();
        let read = self
            .standard_input
            .read_line(&mut line)
            .map_err(Failure::Io)?;
        if line.ends_with('\n') {
            line.pop();
            if line.ends_with('\r') {
                line.pop();
            }
        }
        if read == 0 || line.is_empty() {
            return Err(Failure::credential(format!(
                "Credential standard-input reference '{reference}' is unavailable."
            )));
        }
        Ok(line)
    }

    fn file(&self, reference: &str) -> Result<String, Failure> {
        let path = full_path(Path::new(reference), &self.base_directory);
        let metadata = std::fs::symlink_metadata(&path).map_err(Failure::Io)?;
        if metadata.file_type().is_symlink() {
            return Err(Failure::credential(format!(
                "Credential file reference '{reference}' cannot be a symbolic link."
            )));
        }
        check_owner_only(&metadata, self.platform.os().is_unix(), reference)?;
        let mut bytes = std::fs::read(&path).map_err(Failure::Io)?;
        if bytes.starts_with(b"\xEF\xBB\xBF") {
            bytes.drain(..3);
        }
        let mut text = String::from_utf8(bytes)
            .map_err(|_| Failure::Io(io::Error::from(io::ErrorKind::InvalidData)))?;
        let kept = text.trim_end_matches(['\r', '\n']).len();
        text.truncate(kept);
        if text.is_empty() {
            return Err(Failure::credential(format!(
                "Credential file reference '{reference}' is empty."
            )));
        }
        Ok(text)
    }
}

enum Failure {
    Credential(CredentialError),
    Io(io::Error),
}

impl Failure {
    fn credential(message: String) -> Self {
        Self::Credential(CredentialError::new(message))
    }
}

/// On Unix a credential file must be readable by its owner and by nobody
/// else: any group or other permission bit refuses it.
#[cfg(unix)]
fn check_owner_only(
    metadata: &std::fs::Metadata,
    platform_is_unix: bool,
    reference: &str,
) -> Result<(), Failure> {
    use std::os::unix::fs::PermissionsExt;
    const NON_OWNER_BITS: u32 = 0o077;
    const OWNER_READ: u32 = 0o400;
    if !platform_is_unix {
        return Ok(());
    }
    let mode = metadata.permissions().mode();
    if mode & NON_OWNER_BITS != 0 || mode & OWNER_READ == 0 {
        return Err(Failure::credential(format!(
            "Credential file reference '{reference}' must be readable only by its owner."
        )));
    }
    Ok(())
}

#[cfg(not(unix))]
fn check_owner_only(
    _metadata: &std::fs::Metadata,
    _platform_is_unix: bool,
    _reference: &str,
) -> Result<(), Failure> {
    Ok(())
}

/// One session ready to log in: its descriptor (account replaced when the
/// command line gave one) and its password.
#[derive(Debug)]
pub struct SessionLogin {
    /// The session.
    pub session: SessionDescriptor,
    /// Its password.
    pub password: Secret,
}

/// Resolves every session's password, in document order. With direct
/// credentials (which [`crate::start`] allows only for a one-session
/// document) the command line's account and password replace the session's.
pub fn resolve_session_logins(
    config: &HostConfig,
    direct: Option<DirectCredentials>,
    resolver: &mut CredentialResolver<'_>,
) -> Result<Vec<SessionLogin>, CredentialError> {
    if direct.is_some() && config.sessions.len() != 1 {
        return Err(CredentialError::new(
            "Direct credentials require exactly one configured session.".into(),
        ));
    }
    let mut direct = direct;
    let mut logins = Vec::with_capacity(config.sessions.len());
    for descriptor in &config.sessions {
        let mut session = descriptor.clone();
        let password = match direct.take() {
            Some(DirectCredentials { user, password }) => {
                session.account = user;
                password
            }
            None => resolver.resolve(&session.id, &session.credential)?,
        };
        logins.push(SessionLogin { session, password });
    }
    Ok(logins)
}
