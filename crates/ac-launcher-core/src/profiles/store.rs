//! The profile store: loads (migrating an older launcher's file once),
//! validates, edits and saves the document.

use std::collections::HashSet;
use std::io;
use std::path::{Path, PathBuf};

use ac_host_core::PathSet;
use ac_host_core::json as host_json;

use super::account_name::{find_account, same_account};
use super::character_id;
use super::document::{
    AccountProfile, CharacterProfile, LaunchMode, LauncherProfileDocument, ReadError, ServerProfile,
};
use crate::fsutil;

/// The document version this store writes.
pub const CURRENT_PROFILE_VERSION: i32 = 2;

/// Plugins and logon commands per character, and optionally one user list
/// shared by every server.
pub const PROFILE_VERSION_1: i32 = 1;

/// The file this launcher reads and writes, in the settings folder.
pub const PROFILE_FILE_NAME: &str = "launcher-profiles.v2.json";

/// The file launchers before version 2 read and write; never written by
/// this one.
pub const OLDER_PROFILE_FILE_NAME: &str = "launcher-profiles.json";

/// Why a profile operation failed.
#[derive(Debug, thiserror::Error)]
pub enum ProfileError {
    /// The document or an edit breaks a profile rule; the text says which.
    #[error("{0}")]
    Invalid(String),
    /// A required argument was blank.
    #[error("The value of '{0}' cannot be empty or white space.")]
    BlankArgument(&'static str),
    /// The file could not be read or written.
    #[error("{0}")]
    Io(#[from] io::Error),
}

impl ProfileError {
    fn invalid(message: impl Into<String>) -> Self {
        Self::Invalid(message.into())
    }
}

/// One character from a server's character list.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CharacterRosterEntry {
    /// The character's object id.
    pub id: u32,
    /// The character's name.
    pub name: String,
    /// Seconds until a character pending deletion is gone.
    pub seconds_greyed_out: u32,
}

impl CharacterRosterEntry {
    /// A roster entry.
    pub fn new(id: u32, name: impl Into<String>, seconds_greyed_out: u32) -> Self {
        Self {
            id,
            name: name.into(),
            seconds_greyed_out,
        }
    }
}

/// The profile document and the file it lives in.
#[derive(Debug, Clone)]
pub struct LauncherProfileStore {
    file_path: PathBuf,
    older_file_path: Option<PathBuf>,
    document: LauncherProfileDocument,
    migration_notice: Option<String>,
}

fn require_text(value: &str, argument: &'static str) -> Result<(), ProfileError> {
    if value.trim().is_empty() {
        Err(ProfileError::BlankArgument(argument))
    } else {
        Ok(())
    }
}

fn require_valid_port(port: i32) -> Result<(), ProfileError> {
    if (1..=65535).contains(&port) {
        Ok(())
    } else {
        Err(ProfileError::invalid(format!(
            "Port {port} is outside the valid 1-65535 range."
        )))
    }
}

fn require_loaded_text(value: &str, field: &str) -> Result<(), ProfileError> {
    if value.trim().is_empty() {
        Err(ProfileError::invalid(format!(
            "The {field} cannot be null or whitespace."
        )))
    } else {
        Ok(())
    }
}

pub(crate) fn validate_string_list(
    values: Option<&[String]>,
    value_name: &str,
    require_unique: bool,
) -> Result<(), ProfileError> {
    let Some(values) = values else {
        return Ok(());
    };
    let mut seen = HashSet::new();
    for value in values {
        if value.trim().is_empty() {
            return Err(ProfileError::invalid(format!(
                "A {value_name} cannot be null or whitespace."
            )));
        }
        if require_unique && !seen.insert(value.as_str()) {
            return Err(ProfileError::invalid(format!(
                "The {value_name} '{value}' appears more than once."
            )));
        }
    }
    Ok(())
}

/// A profile tag is a short name with none of the characters the accounts
/// text uses as separators, and appears once per account whatever its case.
pub(crate) fn validate_profile_tags(tags: &[String]) -> Result<(), ProfileError> {
    let mut seen: Vec<&str> = Vec::new();
    for tag in tags {
        if tag.trim().is_empty()
            || tag != tag.trim()
            || tag.contains([',', ';', '=', '"', '#', '\r', '\n', '\t'])
        {
            return Err(ProfileError::invalid(format!(
                "'{tag}' is not a profile name. Use letters, digits and spaces, without , ; = \" \
                 or #."
            )));
        }
        if seen.iter().any(|known| known.eq_ignore_ascii_case(tag)) {
            return Err(ProfileError::invalid(format!(
                "The profile '{tag}' appears more than once."
            )));
        }
        seen.push(tag);
    }
    Ok(())
}

fn normalize_character_id(id: Option<&str>) -> Result<Option<String>, ProfileError> {
    let Some(id) = id.filter(|id| !id.trim().is_empty()) else {
        return Ok(None);
    };
    match character_id::try_parse(Some(id)) {
        Some(parsed) if parsed != 0 => Ok(Some(character_id::to_hex_string(parsed))),
        _ => Err(ProfileError::invalid(
            "Character id must be a non-zero hexadecimal value with a 0x prefix.",
        )),
    }
}

fn character_ids_equal(left: Option<&str>, right: Option<&str>) -> bool {
    match (
        character_id::try_parse(left),
        character_id::try_parse(right),
    ) {
        (Some(left), Some(right)) => left == right,
        _ => false,
    }
}

/// Every account on any server, the first of each name (in any case).
fn distinct_known_accounts(document: &LauncherProfileDocument) -> Vec<&AccountProfile> {
    let mut seen: Vec<&str> = Vec::new();
    document
        .servers
        .iter()
        .flat_map(|server| server.accounts.iter())
        .filter(|account| {
            let new = !seen
                .iter()
                .any(|known| same_account(known, &account.account));
            if new {
                seen.push(&account.account);
            }
            new
        })
        .collect()
}

/// Each saved login and password once, with nothing else of the account:
/// what a new server starts with.
pub(crate) fn copy_known_account_credentials(
    document: &LauncherProfileDocument,
) -> Vec<AccountProfile> {
    distinct_known_accounts(document)
        .into_iter()
        .map(|account| AccountProfile::new(account.account.clone(), account.password.clone()))
        .collect()
}

/// Gives every server whose account list was never reviewed by this
/// launcher the saved accounts it lacks; true when any server changed.
fn initialize_account_lists(document: &mut LauncherProfileDocument) -> bool {
    let known = copy_known_account_credentials(document);
    let mut initialized = false;
    for server in &mut document.servers {
        if server.account_list_initialized {
            continue;
        }
        for account in &known {
            if !server
                .accounts
                .iter()
                .any(|existing| same_account(&existing.account, &account.account))
            {
                server.accounts.push(account.clone());
            }
        }
        server.account_list_initialized = true;
        initialized = true;
    }
    initialized
}

/// The `version` of a profile file, or the reason it has none.
fn read_version(bytes: &[u8], source: &Path) -> Result<i32, ProfileError> {
    let not_valid = || {
        ProfileError::invalid(format!(
            "'{}' is not a valid launcher profile document.",
            source.display()
        ))
    };
    let root = host_json::parse(bytes).map_err(|_| not_valid())?;
    let version = host_json::object_members(&root)
        .ok()
        .and_then(|members| members.iter().find(|member| member.name == "version"))
        .and_then(|member| host_json::int32(&member.value).ok());
    version.ok_or_else(|| {
        ProfileError::invalid(format!(
            "'{}' is not a valid launcher profile document: it has no version.",
            source.display()
        ))
    })
}

impl LauncherProfileStore {
    /// A store for `file_path` holding an empty document until loaded;
    /// `older_file_path` is an older launcher's file, read once to migrate
    /// from when `file_path` does not exist yet, and never written.
    pub fn new(
        file_path: impl AsRef<Path>,
        older_file_path: Option<&Path>,
    ) -> Result<Self, ProfileError> {
        let file_path = file_path.as_ref();
        if file_path.as_os_str().to_string_lossy().trim().is_empty() {
            return Err(ProfileError::BlankArgument("filePath"));
        }
        Ok(Self {
            file_path: fsutil::full_path(file_path),
            older_file_path: older_file_path.map(fsutil::full_path),
            document: LauncherProfileDocument::default(),
            migration_notice: None,
        })
    }

    /// The store for `launcher-profiles.v2.json` in the settings folder,
    /// migrating from `launcher-profiles.json` beside it.
    pub fn for_paths(paths: &PathSet) -> Self {
        Self {
            file_path: fsutil::full_path(&paths.config_directory.join(PROFILE_FILE_NAME)),
            older_file_path: Some(fsutil::full_path(
                &paths.config_directory.join(OLDER_PROFILE_FILE_NAME),
            )),
            document: LauncherProfileDocument::default(),
            migration_notice: None,
        }
    }

    /// The file this store reads and writes.
    pub fn file_path(&self) -> &Path {
        &self.file_path
    }

    /// An older launcher's profile file this store migrates from.
    pub fn older_file_path(&self) -> Option<&Path> {
        self.older_file_path.as_deref()
    }

    /// A copy of the older launcher's file as it was when it was migrated,
    /// kept whatever happens to that file later.
    pub fn version1_backup_path(&self) -> PathBuf {
        let source = self.older_file_path.as_ref().unwrap_or(&self.file_path);
        let stem = source
            .file_stem()
            .map(|stem| stem.to_string_lossy().into_owned())
            .unwrap_or_default();
        source
            .parent()
            .map(Path::to_path_buf)
            .unwrap_or_default()
            .join(format!("{stem}.v1-backup.json"))
    }

    /// What the last load could not carry over from an older file, in
    /// words for the player; `None` when it had nothing to say. Shown once,
    /// after the file is rewritten.
    pub fn migration_notice(&self) -> Option<&str> {
        self.migration_notice.as_deref()
    }

    /// Takes the migration notice, so it is shown once.
    pub fn take_migration_notice(&mut self) -> Option<String> {
        self.migration_notice.take()
    }

    /// The current document.
    pub fn document(&self) -> &LauncherProfileDocument {
        &self.document
    }

    /// The current document, for edits (validated on the next transaction).
    pub fn document_mut(&mut self) -> &mut LauncherProfileDocument {
        &mut self.document
    }

    fn temp_path(&self) -> PathBuf {
        let mut temp = self.file_path.as_os_str().to_owned();
        temp.push(".tmp");
        PathBuf::from(temp)
    }

    /// Loads the profiles. This launcher's own file wins; without one, the
    /// older launcher's file is read once and migrated into it (a version 1
    /// file is kept byte for byte as the backup). Neither file gives an
    /// empty document and `false`. A stale temp file from a crashed save is
    /// removed first; on Unix the file read is made owner-only first. A
    /// failed load leaves the current document as it was.
    pub fn load(&mut self) -> Result<bool, ProfileError> {
        fsutil::try_delete_file(&self.temp_path());
        self.migration_notice = None;
        let source = if self.file_path.exists() {
            self.file_path.clone()
        } else if let Some(older) = self.older_file_path.as_ref().filter(|path| path.exists()) {
            older.clone()
        } else {
            self.document = LauncherProfileDocument::default();
            return Ok(false);
        };
        fsutil::ensure_owner_only(&source).map_err(|_| {
            ProfileError::invalid(format!(
                "'{}' could not be secured to owner-only mode 0600.",
                source.display()
            ))
        })?;
        let bytes = std::fs::read(&source)?;
        let version = read_version(&bytes, &source)?;
        let read = match version {
            CURRENT_PROFILE_VERSION => {
                LauncherProfileDocument::from_json(&bytes).map(|document| (document, Vec::new()))
            }
            PROFILE_VERSION_1 => super::migration::from_version1(&bytes),
            _ => {
                return Err(ProfileError::invalid(format!(
                    "Unsupported launcher-profiles version {version}; expected \
                     {CURRENT_PROFILE_VERSION}."
                )));
            }
        };
        let (mut document, notices) = match read {
            Ok(read) => read,
            Err(ReadError::Json) => {
                return Err(ProfileError::invalid(format!(
                    "'{}' is not a valid launcher profile document.",
                    source.display()
                )));
            }
            Err(ReadError::Empty) => {
                return Err(ProfileError::invalid(format!(
                    "'{}' is empty.",
                    source.display()
                )));
            }
            Err(ReadError::Invalid(error)) => return Err(error),
        };
        validate_and_normalize_document(&mut document)?;
        let initialized = initialize_account_lists(&mut document);
        self.document = document;
        if version == PROFILE_VERSION_1 || source != self.file_path || initialized {
            // A copy of the old file as migrated, whatever an older launcher
            // does to it later.
            let backup = self.version1_backup_path();
            if version == PROFILE_VERSION_1 && !backup.exists() {
                fsutil::write_via_tmp(&backup, &bytes, true)?;
            }
            self.save()?;
            self.migration_notice = (!notices.is_empty()).then(|| {
                format!(
                    "Plugins and logon commands now belong to the account, so every character on \
                     it shares them. Where characters on one account had different logon \
                     commands, the first character's were kept:\n\n{}\n\nThe old file is kept at \
                     {}.",
                    notices.join("\n"),
                    backup.display()
                )
            });
        }
        Ok(true)
    }

    /// Writes the document through a temp file and a rename. The temp file
    /// is owner-only on Unix from its creation. Every server's account
    /// list counts as reviewed from here on.
    pub fn save(&mut self) -> Result<(), ProfileError> {
        for server in &mut self.document.servers {
            server.account_list_initialized = true;
        }
        fsutil::write_via_tmp(&self.file_path, self.document.to_json().as_bytes(), true)?;
        Ok(())
    }

    /// Runs `mutation`, validates and saves; any failure restores the
    /// document exactly as it was before.
    pub fn execute_transaction<T, E>(
        &mut self,
        mutation: impl FnOnce(&mut Self) -> Result<T, E>,
    ) -> Result<T, E>
    where
        E: From<ProfileError>,
    {
        let before = self.document.clone();
        let result = (|| {
            let value = mutation(self)?;
            validate_and_normalize_document(&mut self.document)?;
            self.save()?;
            Ok(value)
        })();
        if result.is_err() {
            self.document = before;
        }
        result
    }

    /// The launcher-wide beta discovery setting.
    pub fn set_show_beta_plugins(&mut self, value: bool) {
        self.document.show_beta_plugins = value;
    }

    fn find_server_index(&self, name: &str) -> Option<usize> {
        self.document
            .servers
            .iter()
            .position(|server| server.name == name)
    }

    fn server_index(&self, name: &str) -> Result<usize, ProfileError> {
        require_text(name, "name")?;
        self.find_server_index(name)
            .ok_or_else(|| ProfileError::invalid(format!("No server named '{name}'.")))
    }

    fn account_index(server: &ServerProfile, account: &str) -> Result<usize, ProfileError> {
        require_text(account, "account")?;
        find_account(&server.accounts, |candidate| &candidate.account, account).ok_or_else(|| {
            ProfileError::invalid(format!(
                "No account '{account}' on server '{}'.",
                server.name
            ))
        })
    }

    fn character_index(
        profile: &AccountProfile,
        character_name: &str,
    ) -> Result<usize, ProfileError> {
        require_text(character_name, "characterName")?;
        profile
            .characters
            .iter()
            .position(|character| character.name == character_name)
            .ok_or_else(|| {
                ProfileError::invalid(format!(
                    "No character '{character_name}' on account '{}'.",
                    profile.account
                ))
            })
    }

    fn locate_account(
        &mut self,
        server_name: &str,
        account: &str,
    ) -> Result<&mut AccountProfile, ProfileError> {
        let server_index = self.server_index(server_name)?;
        let server = &mut self.document.servers[server_index];
        let account_index = Self::account_index(server, account)?;
        Ok(&mut server.accounts[account_index])
    }

    /// Adds a server, starting with each distinct saved login and password
    /// (no characters or settings). Names are unique; the port must be
    /// 1-65535.
    pub fn add_server(&mut self, name: &str, host: &str, port: i32) -> Result<(), ProfileError> {
        require_text(name, "name")?;
        require_text(host, "host")?;
        require_valid_port(port)?;
        if self.find_server_index(name).is_some() {
            return Err(ProfileError::invalid(format!(
                "A server named '{name}' already exists."
            )));
        }
        let mut server = ServerProfile::new(name, host, port);
        server.accounts = copy_known_account_credentials(&self.document);
        server.account_list_initialized = true;
        self.document.servers.push(server);
        Ok(())
    }

    /// Changes a server's name, host or port; everything is checked before
    /// anything changes.
    pub fn edit_server(
        &mut self,
        name: &str,
        new_name: Option<&str>,
        new_host: Option<&str>,
        new_port: Option<i32>,
    ) -> Result<(), ProfileError> {
        let index = self.server_index(name)?;
        if let Some(new_name) = new_name {
            require_text(new_name, "newName")?;
            if new_name != self.document.servers[index].name
                && self.find_server_index(new_name).is_some()
            {
                return Err(ProfileError::invalid(format!(
                    "A server named '{new_name}' already exists."
                )));
            }
        }
        if let Some(new_host) = new_host {
            require_text(new_host, "newHost")?;
        }
        if let Some(new_port) = new_port {
            require_valid_port(new_port)?;
        }
        let server = &mut self.document.servers[index];
        if let Some(new_name) = new_name {
            new_name.clone_into(&mut server.name);
        }
        if let Some(new_host) = new_host {
            new_host.clone_into(&mut server.host);
        }
        if let Some(new_port) = new_port {
            server.port = new_port;
        }
        Ok(())
    }

    /// Removes a server and everything saved under it.
    pub fn remove_server(&mut self, name: &str) -> Result<(), ProfileError> {
        let index = self.server_index(name)?;
        self.document.servers.remove(index);
        Ok(())
    }

    /// Adds an account to a server.
    pub fn add_account(
        &mut self,
        server_name: &str,
        account: &str,
        password: &str,
    ) -> Result<(), ProfileError> {
        require_text(account, "account")?;
        let index = self.server_index(server_name)?;
        let server = &mut self.document.servers[index];
        if server
            .accounts
            .iter()
            .any(|item| same_account(&item.account, account))
        {
            return Err(ProfileError::invalid(format!(
                "Account '{account}' already exists on server '{server_name}'."
            )));
        }
        server.accounts.push(AccountProfile::new(account, password));
        Ok(())
    }

    /// Renames an account or changes its password on one server.
    pub fn edit_account(
        &mut self,
        server_name: &str,
        account: &str,
        new_account: Option<&str>,
        new_password: Option<&str>,
    ) -> Result<(), ProfileError> {
        let server_index = self.server_index(server_name)?;
        let account_index = Self::account_index(&self.document.servers[server_index], account)?;
        if let Some(new_account) = new_account {
            require_text(new_account, "newAccount")?;
            let server = &self.document.servers[server_index];
            // Retyping the account's own name in another case is a rename;
            // taking another account's name, in any case, is a duplicate.
            if new_account != server.accounts[account_index].account
                && server.accounts.iter().enumerate().any(|(index, item)| {
                    index != account_index && same_account(&item.account, new_account)
                })
            {
                return Err(ProfileError::invalid(format!(
                    "Account '{new_account}' already exists on server '{server_name}'."
                )));
            }
        }
        let profile = &mut self.document.servers[server_index].accounts[account_index];
        if let Some(new_account) = new_account {
            new_account.clone_into(&mut profile.account);
        }
        if let Some(new_password) = new_password {
            new_password.clone_into(&mut profile.password);
        }
        Ok(())
    }

    /// The plugins every character on the account launches with, unless it
    /// has its own list.
    pub fn set_account_plugins(
        &mut self,
        server_name: &str,
        account: &str,
        plugins: &[String],
    ) -> Result<(), ProfileError> {
        validate_string_list(Some(plugins), "plugin", true)?;
        self.locate_account(server_name, account)?.plugins = plugins.to_vec();
        Ok(())
    }

    /// The account's profile tags, which the main window filters by.
    pub fn set_account_profiles(
        &mut self,
        server_name: &str,
        account: &str,
        profiles: &[String],
    ) -> Result<(), ProfileError> {
        validate_profile_tags(profiles)?;
        self.locate_account(server_name, account)?.profiles = profiles.to_vec();
        Ok(())
    }

    /// Commands run in order after any character on the account logs in.
    pub fn set_account_login_commands(
        &mut self,
        server_name: &str,
        account: &str,
        commands: &[String],
    ) -> Result<(), ProfileError> {
        validate_string_list(Some(commands), "login command", false)?;
        self.locate_account(server_name, account)?.login_commands = commands.to_vec();
        Ok(())
    }

    /// Gives a character its own plugin list, or with `None` puts it back
    /// on its account's.
    pub fn set_character_plugins(
        &mut self,
        server_name: &str,
        account: &str,
        character_name: &str,
        plugins: Option<&[String]>,
    ) -> Result<(), ProfileError> {
        validate_string_list(plugins, "plugin", true)?;
        let profile = self.locate_account(server_name, account)?;
        let index = Self::character_index(profile, character_name)?;
        profile.characters[index].plugins = plugins.map(<[String]>::to_vec);
        Ok(())
    }

    /// Remembers what an account's row is set to launch.
    pub fn edit_account_selection(
        &mut self,
        server_name: &str,
        account: &str,
        selected_character: Option<&str>,
        selected_launch_mode: LaunchMode,
    ) -> Result<(), ProfileError> {
        let profile = self.locate_account(server_name, account)?;
        if let Some(selected) = selected_character
            && !profile
                .characters
                .iter()
                .any(|character| character.name == selected)
        {
            return Err(ProfileError::invalid(format!(
                "Character '{selected}' is not on account '{account}'."
            )));
        }
        profile.selected_character = selected_character.map(str::to_owned);
        profile.selected_launch_mode = Some(selected_launch_mode);
        Ok(())
    }

    /// Removes an account from one server.
    pub fn remove_account(&mut self, server_name: &str, account: &str) -> Result<(), ProfileError> {
        let server_index = self.server_index(server_name)?;
        let account_index = Self::account_index(&self.document.servers[server_index], account)?;
        self.document.servers[server_index]
            .accounts
            .remove(account_index);
        Ok(())
    }

    /// Adds a character by hand. A blank id means unknown; `plugins` of
    /// `None` uses the account's.
    pub fn add_character(
        &mut self,
        server_name: &str,
        account: &str,
        character_name: &str,
        id: Option<&str>,
        launch_mode: LaunchMode,
        plugins: Option<&[String]>,
    ) -> Result<(), ProfileError> {
        require_text(character_name, "characterName")?;
        validate_string_list(plugins, "plugin", true)?;
        let profile = self.locate_account(server_name, account)?;
        if profile
            .characters
            .iter()
            .any(|character| character.name == character_name)
        {
            return Err(ProfileError::invalid(format!(
                "Character '{character_name}' already exists on account '{account}'."
            )));
        }
        let normalized = normalize_character_id(id)?;
        if let Some(normalized) = &normalized
            && profile
                .characters
                .iter()
                .any(|character| character_ids_equal(character.id.as_deref(), Some(normalized)))
        {
            return Err(ProfileError::invalid(format!(
                "Character id '{normalized}' already exists on account '{account}'."
            )));
        }
        profile.characters.push(CharacterProfile {
            name: character_name.to_owned(),
            id: normalized,
            launch_mode,
            plugins: plugins.map(<[String]>::to_vec),
        });
        Ok(())
    }

    /// Changes a character's launch mode, name or id; everything is checked
    /// before anything changes. `new_id` of `Some("")` clears the id.
    pub fn edit_character(
        &mut self,
        server_name: &str,
        account: &str,
        character_name: &str,
        launch_mode: Option<LaunchMode>,
        new_name: Option<&str>,
        new_id: Option<&str>,
    ) -> Result<(), ProfileError> {
        let profile = self.locate_account(server_name, account)?;
        let index = Self::character_index(profile, character_name)?;
        if let Some(new_name) = new_name {
            require_text(new_name, "newName")?;
            if new_name != profile.characters[index].name
                && profile
                    .characters
                    .iter()
                    .any(|character| character.name == new_name)
            {
                return Err(ProfileError::invalid(format!(
                    "Character '{new_name}' already exists on account '{account}'."
                )));
            }
        }
        let mut normalized_id = None;
        if let Some(new_id) = new_id {
            normalized_id = normalize_character_id(Some(new_id))?;
            if let Some(normalized) = &normalized_id
                && profile
                    .characters
                    .iter()
                    .enumerate()
                    .any(|(other, candidate)| {
                        other != index
                            && character_ids_equal(candidate.id.as_deref(), Some(normalized))
                    })
            {
                return Err(ProfileError::invalid(format!(
                    "Character id '{normalized}' already exists on account '{account}'."
                )));
            }
        }
        let character = &mut profile.characters[index];
        if let Some(new_name) = new_name {
            new_name.clone_into(&mut character.name);
        }
        if new_id.is_some() {
            character.id = normalized_id;
        }
        if let Some(launch_mode) = launch_mode {
            character.launch_mode = launch_mode;
        }
        Ok(())
    }

    /// Removes a character.
    pub fn remove_character(
        &mut self,
        server_name: &str,
        account: &str,
        character_name: &str,
    ) -> Result<(), ProfileError> {
        let profile = self.locate_account(server_name, account)?;
        let index = Self::character_index(profile, character_name)?;
        profile.characters.remove(index);
        Ok(())
    }

    /// Folds a server's character list into the account: a character found
    /// by id or by name takes the roster's id and name and keeps its
    /// settings (duplicates matching the same entry collapse into it); a new
    /// one launches to the character screen with its account's plugins.
    /// Characters missing from the roster stay.
    pub fn merge_roster(
        &mut self,
        server_name: &str,
        account: &str,
        roster: &[CharacterRosterEntry],
    ) -> Result<(), ProfileError> {
        let profile = self.locate_account(server_name, account)?;
        let mut ids = HashSet::new();
        let mut names = HashSet::new();
        for entry in roster {
            if entry.id == 0 {
                return Err(ProfileError::invalid(
                    "A roster character id cannot be zero.",
                ));
            }
            require_text(&entry.name, "Name")?;
            if !ids.insert(entry.id) || !names.insert(entry.name.as_str()) {
                return Err(ProfileError::invalid(
                    "The reported character roster contains a duplicate id or name.",
                ));
            }
        }

        for entry in roster {
            let id_text = character_id::to_hex_string(entry.id);
            let id_matches = |character: &CharacterProfile| {
                character_id::try_parse(character.id.as_deref()) == Some(entry.id)
            };
            let matches: Vec<usize> = profile
                .characters
                .iter()
                .enumerate()
                .filter(|(_, character)| id_matches(character) || character.name == entry.name)
                .map(|(index, _)| index)
                .collect();
            let existing = matches
                .iter()
                .copied()
                .find(|&index| id_matches(&profile.characters[index]))
                .or_else(|| matches.first().copied());
            if let Some(existing) = existing {
                let character = &mut profile.characters[existing];
                character.id = Some(id_text);
                character.name.clone_from(&entry.name);
                for &duplicate in matches.iter().rev() {
                    if duplicate != existing {
                        profile.characters.remove(duplicate);
                    }
                }
                continue;
            }
            profile.characters.push(CharacterProfile {
                name: entry.name.clone(),
                id: Some(id_text),
                launch_mode: LaunchMode::GuiSelect,
                plugins: None,
            });
        }
        Ok(())
    }
}

/// The document rules every load and every transaction enforces; character
/// ids are rewritten in the canonical `0x%08X` form.
pub(crate) fn validate_and_normalize_document(
    document: &mut LauncherProfileDocument,
) -> Result<(), ProfileError> {
    let mut server_names = HashSet::new();
    for server in &mut document.servers {
        require_loaded_text(&server.name, "server name")?;
        require_loaded_text(&server.host, &format!("host for server '{}'", server.name))?;
        require_valid_port(server.port)?;
        if !server_names.insert(server.name.clone()) {
            return Err(ProfileError::invalid(format!(
                "A server named '{}' appears more than once.",
                server.name
            )));
        }
        // Exact: a file saved before account names were compared without
        // case can hold two spellings of one account, and both are kept.
        // Adding and renaming refuse another spelling.
        let mut account_names = HashSet::new();
        for account in &mut server.accounts {
            require_loaded_text(&account.account, "account name")?;
            if !account_names.insert(account.account.clone()) {
                return Err(ProfileError::invalid(format!(
                    "Account '{}' appears more than once on server '{}'.",
                    account.account, server.name
                )));
            }
            validate_string_list(Some(&account.plugins), "plugin", true)?;
            validate_string_list(Some(&account.login_commands), "login command", false)?;
            validate_profile_tags(&account.profiles)?;
            let mut character_names = HashSet::new();
            let mut character_ids = HashSet::new();
            let mut normalized = Vec::new();
            for (index, character) in account.characters.iter().enumerate() {
                require_loaded_text(&character.name, "character name")?;
                if !character_names.insert(character.name.as_str()) {
                    return Err(ProfileError::invalid(format!(
                        "Character '{}' appears more than once on account '{}'.",
                        character.name, account.account
                    )));
                }
                if let Some(id_text) = &character.id {
                    let id = character_id::try_parse(Some(id_text))
                        .filter(|&id| id != 0)
                        .ok_or_else(|| {
                            ProfileError::invalid(format!(
                                "Character '{}' has an invalid id '{id_text}'.",
                                character.name
                            ))
                        })?;
                    if !character_ids.insert(id) {
                        return Err(ProfileError::invalid(format!(
                            "Character id '{id_text}' appears more than once on account '{}'.",
                            account.account
                        )));
                    }
                    normalized.push((index, id));
                }
                validate_string_list(character.plugins.as_deref(), "plugin", true)?;
            }
            for (index, id) in normalized {
                account.characters[index].id = Some(character_id::to_hex_string(id));
            }
        }
    }
    Ok(())
}
