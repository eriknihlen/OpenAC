//! Which account rows were ticked for Play selected, remembered across
//! restarts apart from the credential profiles
//! (`launcher-checked-accounts.json` in the settings folder).

use std::collections::BTreeSet;
use std::path::{Path, PathBuf};

use ac_host_core::PathSet;
use ac_host_core::json as host_json;

use super::account_name::same_account;
use crate::fsutil;
use crate::json::{self, ObjectBuilder, Out};

/// The file's name in the settings folder.
pub const CHECKED_ACCOUNTS_FILE_NAME: &str = "launcher-checked-accounts.json";

/// The server and account of one checked launch row, as they were shown.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct LauncherCheckedAccount {
    /// The server's name.
    pub server: String,
    /// The account's name.
    pub account: String,
}

impl LauncherCheckedAccount {
    /// A checked row.
    pub fn new(server: impl Into<String>, account: impl Into<String>) -> Self {
        Self {
            server: server.into(),
            account: account.into(),
        }
    }
}

/// Why the checked rows could not be read or written.
#[derive(Debug, thiserror::Error)]
#[error("{0}")]
pub struct CheckedAccountsError(pub String);

/// The remembered checked rows.
#[derive(Debug, Clone)]
pub struct LauncherCheckedAccountStore {
    file_path: PathBuf,
    checked: BTreeSet<LauncherCheckedAccount>,
}

impl LauncherCheckedAccountStore {
    /// A store for `file_path`, empty until loaded.
    pub fn new(file_path: &Path) -> Self {
        Self {
            file_path: fsutil::full_path(file_path),
            checked: BTreeSet::new(),
        }
    }

    /// The store in the settings folder.
    pub fn for_paths(paths: &PathSet) -> Self {
        Self::new(&paths.config_directory.join(CHECKED_ACCOUNTS_FILE_NAME))
    }

    /// The file.
    pub fn file_path(&self) -> &Path {
        &self.file_path
    }

    /// Whether the row of `account` on `server` was checked: the server
    /// exactly, the account in any case.
    pub fn contains(&self, server: &str, account: &str) -> bool {
        self.checked
            .iter()
            .any(|row| row.server == server && same_account(&row.account, account))
    }

    /// Reads the file; a missing file is no checked rows. A file that is
    /// not a list of rows each naming a server and an account is an error
    /// and is left as it is.
    pub fn load(&mut self) -> Result<(), CheckedAccountsError> {
        let bytes = match std::fs::read(&self.file_path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                self.checked.clear();
                return Ok(());
            }
            Err(error) => return Err(CheckedAccountsError(error.to_string())),
        };
        let root =
            host_json::parse(&bytes).map_err(|error| CheckedAccountsError(error.to_string()))?;
        if host_json::is_null(&root) {
            return Err(CheckedAccountsError(
                "The checked-account selection file is empty.".into(),
            ));
        }
        let items = host_json::array_items(&root)
            .map_err(|error| CheckedAccountsError(error.to_string()))?;
        let invalid =
            || CheckedAccountsError("Each checked account needs a server and account name.".into());
        let mut rows = BTreeSet::new();
        for item in items {
            if host_json::is_null(item) {
                return Err(invalid());
            }
            let members = host_json::object_members(item)
                .map_err(|error| CheckedAccountsError(error.to_string()))?;
            let text = |name: &str| -> Result<Option<String>, CheckedAccountsError> {
                match members.iter().find(|member| member.name == name) {
                    Some(member) => host_json::string_or_null(&member.value)
                        .map_err(|error| CheckedAccountsError(error.to_string())),
                    None => Ok(None),
                }
            };
            let server = text("server")?.filter(|value| !value.trim().is_empty());
            let account = text("account")?.filter(|value| !value.trim().is_empty());
            let (Some(server), Some(account)) = (server, account) else {
                return Err(invalid());
            };
            rows.insert(LauncherCheckedAccount { server, account });
        }
        self.checked = rows;
        Ok(())
    }

    /// Writes `rows` (in server, then account order) and remembers them.
    pub fn save(
        &mut self,
        rows: impl IntoIterator<Item = LauncherCheckedAccount>,
    ) -> Result<(), CheckedAccountsError> {
        let selected: BTreeSet<LauncherCheckedAccount> = rows.into_iter().collect();
        let document = Out::Array(
            selected
                .iter()
                .map(|row| {
                    ObjectBuilder::new()
                        .member("server", Out::str(&row.server))
                        .member("account", Out::str(&row.account))
                        .build()
                })
                .collect(),
        );
        fsutil::write_via_tmp(
            &self.file_path,
            json::to_pretty(&document).as_bytes(),
            false,
        )
        .map_err(|error| CheckedAccountsError(error.to_string()))?;
        self.checked = selected;
        Ok(())
    }
}
