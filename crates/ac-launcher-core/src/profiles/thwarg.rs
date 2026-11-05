//! Import from the Thwarg launcher: its servers, accounts (with their
//! passwords), characters, profiles and logon commands, read from the Thwarg
//! launcher's files and merged into the launcher profiles.
//!
//! [`read_folder`] is the only part that touches the disk; [`import`] works
//! on the file contents and a profile document, so a preview runs it on a
//! copy of the document and the import on the real one inside a profile
//! transaction.
//!
//! The mapping:
//! - an account is added to every server it is used with (a character list,
//!   a logon command file, or a profile that has it active or picks a
//!   character for it there); a launcher server of that name is used, else
//!   one from Thwarg's own server list or its published one (the own list
//!   wins), matched to a launcher server by host and port, or added;
//! - an account already on that server is not overwritten: it only gains
//!   the characters it lacks, and logon commands for characters with none
//!   of their own; it is not put in any profile;
//! - each Thwarg profile is a profile here (added to the profile list even
//!   with nothing in it); on a new account it ticks (the account itself,
//!   and its setting for that server) it goes on the character the profile
//!   picks for that account, or on the account when the profile picks the
//!   character screen;
//! - the row's character comes from the profile activated last;
//! - each character keeps its own Thwarg logon commands; the account's
//!   shared commands are not touched.
//!
//! Passwords are never part of the report or of any `Debug` output.

use std::collections::{BTreeMap, BTreeSet};
use std::fmt;
use std::io::{self, Read};
use std::path::{Path, PathBuf};

use ac_host_core::json::{self as host_json, Node, Value};

use super::account_name::same_account;
use super::character_id;
use super::document::{
    AccountProfile, CharacterProfile, LaunchMode, LauncherProfileDocument, ServerProfile,
};
use super::store::validate_profile_tags;

/// The Thwarg launcher's data folder name, under the roaming application
/// data folder on Windows.
pub const FOLDER_NAME: &str = "ThwargLauncher";

/// The keys an account line may carry. The password is split off only
/// before one of these, so a password may hold commas.
const ACCOUNT_KEYS: [&str; 5] = ["Name", "Password", "LaunchPath", "PreferencePath", "Alias"];

/// The literal a profile writes for "the character screen".
const CHARACTER_SCREEN: &str = "None";

/// The Thwarg launcher's data folder of a usual Windows install, or `None`
/// (always `None` elsewhere: there the folder is typed).
pub fn find_folder() -> Option<PathBuf> {
    if !cfg!(windows) {
        return None;
    }
    let roaming = std::env::var_os("APPDATA").filter(|value| !value.is_empty())?;
    let folder = PathBuf::from(roaming).join(FOLDER_NAME);
    folder.is_dir().then_some(folder)
}

/// One text file: its name (without folder) and contents.
#[derive(Clone, PartialEq, Eq)]
pub struct ThwargFile {
    /// The file name, such as `main.txt`.
    pub name: String,
    /// Its text, without a byte-order mark.
    pub text: String,
}

impl ThwargFile {
    /// A file named `name` holding `text`.
    pub fn new(name: impl Into<String>, text: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            text: text.into(),
        }
    }
}

impl fmt::Debug for ThwargFile {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ThwargFile")
            .field("name", &self.name)
            .finish_non_exhaustive()
    }
}

/// The files an import reads. Missing files are `None` or absent.
#[derive(Clone, Default, PartialEq, Eq)]
pub struct ThwargFiles {
    /// `Accounts.txt`: account names and passwords.
    pub accounts: Option<String>,
    /// `Servers\UserServerList.xml`.
    pub user_servers: Option<String>,
    /// `Servers\PublishedWildWestServerList.xml`.
    pub published_servers: Option<String>,
    /// `characters\characters_*.txt`.
    pub characters: Vec<ThwargFile>,
    /// `Profiles\*.txt`.
    pub profiles: Vec<ThwargFile>,
    /// `LoginCommands\LoginCommands*.txt`, the global file included.
    pub login_commands: Vec<ThwargFile>,
    /// Files that were there but could not be read, each with why.
    pub unreadable: Vec<String>,
}

impl fmt::Debug for ThwargFiles {
    // The accounts file holds passwords: only counts are shown.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ThwargFiles")
            .field("has_accounts", &self.accounts.is_some())
            .field("has_user_servers", &self.user_servers.is_some())
            .field("has_published_servers", &self.published_servers.is_some())
            .field("characters", &self.characters.len())
            .field("profiles", &self.profiles.len())
            .field("login_commands", &self.login_commands.len())
            .field("unreadable", &self.unreadable)
            .finish()
    }
}

/// The most an import reads: per file, and files per folder.
#[derive(Debug, Clone, Copy)]
struct Limits {
    file_bytes: u64,
    files_per_folder: usize,
}

/// 4 MiB per file and 10,000 files per folder: far above any real Thwarg
/// install, low enough that a wrong folder cannot fill memory.
const LIMITS: Limits = Limits {
    file_bytes: 4 * 1024 * 1024,
    files_per_folder: 10_000,
};

/// Reads the files an import needs from the Thwarg launcher's data folder.
/// Only a missing folder is an error; a missing file is left out, and a
/// file that cannot be read (or is too large, or one past the per-folder
/// limit) is listed in [`ThwargFiles::unreadable`].
pub fn read_folder(folder: &Path) -> io::Result<ThwargFiles> {
    read_folder_within(folder, LIMITS)
}

fn read_folder_within(folder: &Path, limits: Limits) -> io::Result<ThwargFiles> {
    if !folder.is_dir() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            format!("The folder {} was not found.", folder.display()),
        ));
    }
    let mut files = ThwargFiles::default();
    let mut unreadable = Vec::new();
    let one = |path: PathBuf, shown: &str, unreadable: &mut Vec<String>| {
        read_one(&path, shown, limits, unreadable)
    };
    files.accounts = one(folder.join("Accounts.txt"), "Accounts.txt", &mut unreadable);
    let servers = folder.join("Servers");
    files.user_servers = one(
        servers.join("UserServerList.xml"),
        r"Servers\UserServerList.xml",
        &mut unreadable,
    );
    files.published_servers = one(
        servers.join("PublishedWildWestServerList.xml"),
        r"Servers\PublishedWildWestServerList.xml",
        &mut unreadable,
    );
    files.characters = read_all(
        &folder.join("characters"),
        "characters",
        |name| name.starts_with("characters_"),
        limits,
        &mut unreadable,
    );
    files.profiles = read_all(
        &folder.join("Profiles"),
        "Profiles",
        |_| true,
        limits,
        &mut unreadable,
    );
    files.login_commands = read_all(
        &folder.join("LoginCommands"),
        "LoginCommands",
        |name| name.starts_with("logincommands"),
        limits,
        &mut unreadable,
    );
    files.unreadable = unreadable;
    Ok(files)
}

fn read_one(
    path: &Path,
    shown: &str,
    limits: Limits,
    unreadable: &mut Vec<String>,
) -> Option<String> {
    let read = std::fs::File::open(path).and_then(|file| {
        let mut bytes = Vec::new();
        file.take(limits.file_bytes + 1).read_to_end(&mut bytes)?;
        Ok(bytes)
    });
    match read {
        Ok(bytes) if bytes.len() as u64 > limits.file_bytes => {
            unreadable.push(format!("{shown}: larger than {} bytes", limits.file_bytes));
            None
        }
        Ok(bytes) => decode(bytes, shown, unreadable),
        Err(error) if error.kind() == io::ErrorKind::NotFound => None,
        Err(error) => {
            unreadable.push(format!("{shown}: {error}"));
            None
        }
    }
}

/// The `.txt` files of `directory` whose lowercase name `wanted` accepts,
/// by name, at most `limits.files_per_folder` of them.
fn read_all(
    directory: &Path,
    shown: &str,
    wanted: impl Fn(&str) -> bool,
    limits: Limits,
    unreadable: &mut Vec<String>,
) -> Vec<ThwargFile> {
    let entries = match std::fs::read_dir(directory) {
        Ok(entries) => entries,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Vec::new(),
        Err(error) => {
            unreadable.push(format!("{shown}: {error}"));
            return Vec::new();
        }
    };
    let mut names: Vec<String> = entries
        .flatten()
        .filter(|entry| entry.path().is_file())
        .filter_map(|entry| entry.file_name().into_string().ok())
        .filter(|name| {
            let lower = name.to_lowercase();
            lower.ends_with(".txt") && wanted(&lower)
        })
        .collect();
    names.sort();
    if names.len() > limits.files_per_folder {
        let skipped = names.split_off(limits.files_per_folder);
        unreadable.push(format!(
            "{shown}: more than {} files; {} from {} on",
            limits.files_per_folder,
            skipped.len(),
            skipped[0]
        ));
    }
    names
        .into_iter()
        .filter_map(|name| {
            let shown_name = format!(r"{shown}\{name}");
            let text = read_one(&directory.join(&name), &shown_name, limits, unreadable)?;
            Some(ThwargFile { name, text })
        })
        .collect()
}

fn decode(bytes: Vec<u8>, shown: &str, unreadable: &mut Vec<String>) -> Option<String> {
    match String::from_utf8(bytes) {
        Ok(text) => Some(strip_bom(&text).to_owned()),
        Err(_) => {
            unreadable.push(format!("{shown}: not UTF-8 text"));
            None
        }
    }
}

fn strip_bom(text: &str) -> &str {
    text.strip_prefix('\u{feff}').unwrap_or(text)
}

// --- Parsing -----------------------------------------------------------------------------

/// One line of `Accounts.txt`.
#[derive(Clone, PartialEq, Eq)]
struct ThwargAccount {
    name: String,
    password: String,
}

impl fmt::Debug for ThwargAccount {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ThwargAccount")
            .field("name", &self.name)
            .finish_non_exhaustive()
    }
}

/// The accounts of `Accounts.txt`, first spelling of each name kept; a
/// line that cannot be used is reported (never with its text, which may
/// hold a password).
fn parse_accounts(text: &str, report: &mut Vec<String>) -> Vec<ThwargAccount> {
    let mut accounts: Vec<ThwargAccount> = Vec::new();
    for (index, line) in strip_bom(text).lines().enumerate() {
        let line = line.trim_end_matches('\r').trim_start();
        if line.trim().is_empty() || line.starts_with('#') {
            continue;
        }
        let fields = account_fields(line);
        let field = |key: &str| {
            fields
                .iter()
                .find(|(name, _)| name.eq_ignore_ascii_case(key))
                .map(|(_, value)| *value)
        };
        if fields.len() == 1 && field("Version").is_some() {
            continue;
        }
        let Some(name) = field("Name").map(str::trim).filter(|name| !name.is_empty()) else {
            report.push(format!(
                "Accounts.txt line {}: no account name; the line was skipped.",
                index + 1
            ));
            continue;
        };
        // A malformed line can leave the password inside the name; such a
        // name is never shown.
        if name.contains(['=', ';']) {
            report.push(format!(
                "Accounts.txt line {}: the account name is not readable; the line was skipped.",
                index + 1
            ));
            continue;
        }
        if accounts
            .iter()
            .any(|account| same_account(&account.name, name))
        {
            report.push(format!(
                "Accounts.txt line {}: {name} appears again; the first line was used.",
                index + 1
            ));
            continue;
        }
        accounts.push(ThwargAccount {
            name: name.to_owned(),
            password: field("Password").unwrap_or_default().to_owned(),
        });
    }
    accounts
}

/// Splits `Key=Value,Key=Value`. A value ends at a comma followed by a key
/// (letters and `=`); a password ends only at a comma followed by one of
/// the keys an account line may carry, so it may hold commas.
fn account_fields(line: &str) -> Vec<(&str, &str)> {
    let mut fields = Vec::new();
    let mut rest = line;
    while let Some(equals) = rest.find('=') {
        let key = rest[..equals].trim();
        let value_start = equals + 1;
        let password = key.eq_ignore_ascii_case("Password");
        let end = rest[value_start..]
            .match_indices(',')
            .map(|(offset, _)| value_start + offset)
            .find(|&comma| starts_field(&rest[comma + 1..], password))
            .unwrap_or(rest.len());
        fields.push((key, &rest[value_start..end]));
        if end == rest.len() {
            break;
        }
        rest = &rest[end + 1..];
    }
    fields
}

fn starts_field(text: &str, known_only: bool) -> bool {
    let Some(equals) = text.find('=') else {
        return false;
    };
    let key = &text[..equals];
    if known_only {
        ACCOUNT_KEYS
            .iter()
            .any(|known| known.eq_ignore_ascii_case(key))
    } else {
        !key.is_empty() && key.bytes().all(|byte| byte.is_ascii_alphabetic())
    }
}

/// One server of a Thwarg server list.
#[derive(Debug, Clone, PartialEq, Eq)]
struct ThwargServer {
    name: String,
    /// Host and port, or why `connect_string` cannot be used.
    endpoint: Result<(String, i32), String>,
}

/// The servers of one server list file.
fn parse_server_list(text: &str, file: &str, report: &mut Vec<String>) -> Vec<ThwargServer> {
    let document = match roxmltree::Document::parse(strip_bom(text)) {
        Ok(document) => document,
        Err(error) => {
            report.push(format!("{file}: not a readable server list ({error})."));
            return Vec::new();
        }
    };
    document
        .root_element()
        .children()
        .filter(|node| node.has_tag_name("ServerItem"))
        .filter_map(|item| {
            let child = |tag: &str| {
                item.children()
                    .find(|node| node.has_tag_name(tag))
                    .and_then(|node| node.text())
                    .map(str::trim)
                    .unwrap_or_default()
            };
            let name = child("name");
            if name.is_empty() {
                return None;
            }
            Some(ThwargServer {
                name: name.to_owned(),
                endpoint: parse_connect_string(child("connect_string")),
            })
        })
        .collect()
}

/// `host:port`.
fn parse_connect_string(text: &str) -> Result<(String, i32), String> {
    if text.trim().is_empty() {
        return Err("it has no address".into());
    }
    let unusable = || format!("its address '{text}' is not host:port");
    let (host, port) = text.trim().rsplit_once(':').ok_or_else(unusable)?;
    let host = host.trim();
    let port = port.trim();
    if host.is_empty() || port.is_empty() || !port.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err(unusable());
    }
    let port = port
        .parse::<i32>()
        .ok()
        .filter(|port| (1..=65535).contains(port))
        .ok_or_else(unusable)?;
    Ok((host.to_owned(), port))
}

/// The characters one file lists for an account on a server.
#[derive(Debug, Clone, PartialEq, Eq)]
struct ThwargCharacters {
    server: String,
    account: String,
    characters: Vec<(String, Option<u32>)>,
}

/// A characters file: `{ "<Server>-<account>": { "ZoneId": "<account>",
/// "CharacterList": [ { "Id", "Name" } ] } }`. The server is the key less
/// `-` and the zone id at its end, so names with `_` or `-` read right.
fn parse_characters(file: &ThwargFile, report: &mut Vec<String>) -> Vec<ThwargCharacters> {
    let unreadable = |report: &mut Vec<String>| {
        report.push(format!(
            r"characters\{}: not a readable character list; skipped.",
            file.name
        ));
    };
    let Ok(root) = host_json::parse(file.text.as_bytes()) else {
        unreadable(report);
        return Vec::new();
    };
    let Ok(members) = host_json::object_members(&root) else {
        unreadable(report);
        return Vec::new();
    };
    let mut result = Vec::new();
    for member in members {
        let zone = member_text(&member.value, "ZoneId").unwrap_or_default();
        let key = member.name.as_str();
        // The zone id may be lowercased: compare without case, at each `-`.
        let zone = zone.to_lowercase();
        let split = key
            .match_indices('-')
            .map(|(dash, _)| dash + 1)
            .find(|&at| at > 1 && !zone.is_empty() && key[at..].to_lowercase() == zone);
        let Some(at) = split else {
            report.push(format!(
                r"characters\{}: the entry '{key}' does not name its server and account; skipped.",
                file.name
            ));
            continue;
        };
        let characters = member_node(&member.value, "CharacterList")
            .and_then(|list| host_json::array_items(list).ok())
            .unwrap_or_default()
            .iter()
            .filter_map(|character| {
                let name = member_text(character, "Name")?.trim().to_owned();
                if name.is_empty() {
                    return None;
                }
                let id = member_node(character, "Id")
                    .and_then(|id| match &id.value {
                        Value::Number(text) => text.parse::<u32>().ok(),
                        _ => None,
                    })
                    .filter(|&id| id != 0);
                Some((name, id))
            })
            .collect();
        result.push(ThwargCharacters {
            server: key[..at - 1].to_owned(),
            account: key[at..].to_owned(),
            characters,
        });
    }
    result
}

/// One profile's settings for an account on a server.
#[derive(Debug, Clone, PartialEq, Eq)]
struct CharacterSetting {
    account: String,
    server: String,
    active: bool,
    /// `None` for the character screen.
    chosen: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct ThwargProfile {
    name: String,
    file_name: String,
    /// Milliseconds since 1970, from `LastActivatedDate`.
    last_activated: Option<i64>,
    account_states: Vec<(String, bool)>,
    settings: Vec<CharacterSetting>,
}

impl ThwargProfile {
    fn setting(&self, account: &str, server: &str) -> Option<&CharacterSetting> {
        self.settings.iter().find(|setting| {
            same_account(&setting.account, account) && setting.server.eq_ignore_ascii_case(server)
        })
    }

    /// Whether the account is in this profile on `server`: its account
    /// state is ticked and so is its setting for that server. Thwarg keeps
    /// the setting ticked for most accounts in every profile, so the
    /// account state is what says the account belongs to the profile; no
    /// setting for a server means not on that server.
    fn is_active(&self, account: &str, server: &str) -> bool {
        let ticked = self
            .account_states
            .iter()
            .any(|(name, active)| *active && same_account(name, account));
        ticked
            && self
                .setting(account, server)
                .is_some_and(|setting| setting.active)
    }
}

fn parse_profile(file: &ThwargFile, report: &mut Vec<String>) -> Option<ThwargProfile> {
    let Ok(root) = host_json::parse(file.text.as_bytes()) else {
        report.push(format!(
            r"Profiles\{}: not a readable profile; skipped.",
            file.name
        ));
        return None;
    };
    let stem = file
        .name
        .rsplit_once('.')
        .map_or(file.name.as_str(), |(stem, _)| stem);
    let name = member_text(&root, "Name")
        .map(|name| name.trim().to_owned())
        .filter(|name| !name.is_empty())
        .unwrap_or_else(|| stem.to_owned());
    let items = |member: &str| {
        member_node(&root, member)
            .and_then(|list| host_json::array_items(list).ok())
            .unwrap_or_default()
    };
    let account_states = items("AccountStates")
        .iter()
        .filter_map(|state| {
            Some((
                member_text(state, "AccountName")?,
                member_bool(state, "Active"),
            ))
        })
        .collect();
    let settings = items("CharacterSettings")
        .iter()
        .filter_map(|setting| {
            let account = member_text(setting, "AccountName")?;
            let server = member_text(setting, "ServerName")?;
            if account.trim().is_empty() || server.trim().is_empty() {
                return None;
            }
            Some(CharacterSetting {
                account,
                server,
                active: member_bool(setting, "Active"),
                chosen: member_text(setting, "ChosenCharacter")
                    .map(|chosen| chosen.trim().to_owned())
                    .filter(|chosen| !chosen.is_empty() && chosen != CHARACTER_SCREEN),
            })
        })
        .collect();
    Some(ThwargProfile {
        name,
        file_name: file.name.clone(),
        last_activated: member_text(&root, "LastActivatedDate")
            .as_deref()
            .and_then(parse_json_date),
        account_states,
        settings,
    })
}

/// `/Date(1700000000000)/` or `/Date(1700000000000+0100)/`: milliseconds
/// since 1970.
fn parse_json_date(text: &str) -> Option<i64> {
    let rest = text.split_once("Date(")?.1;
    let sign = usize::from(rest.starts_with('-'));
    let digits = rest[sign..]
        .find(|c: char| !c.is_ascii_digit())
        .map_or(rest.len(), |end| end + sign);
    rest[..digits].parse().ok()
}

/// One logon command file.
#[derive(Debug, Clone, PartialEq, Eq)]
struct ThwargCommands {
    file_name: String,
    /// What follows `LoginCommands-`: `<account>-<server>-<character>`;
    /// `None` for the global file.
    owner_text: Option<String>,
    /// The account, server and character, once [`split_owner`] told them
    /// apart.
    owner: Option<(String, String, String)>,
    wait_ms: Option<u32>,
    commands: Vec<String>,
}

impl ThwargCommands {
    /// The character the file is for; empty for the global file.
    fn character(&self) -> &str {
        self.owner
            .as_ref()
            .map_or("", |(_, _, character)| character.as_str())
    }
}

/// `LoginCommands-<account>-<server>-<character>.txt` or
/// `LoginCommandsGlobal.txt`, with lines `WaitMilliseconds:N`,
/// `CommandCount:N` and `Command0:` to `Command<N-1>:`. Any of the three
/// names may hold `-`, so the name is split later, against the accounts
/// and servers the files know ([`split_owner`]).
fn parse_commands(file: &ThwargFile, report: &mut Vec<String>) -> Option<ThwargCommands> {
    const PREFIX: &str = "LoginCommands-";
    let stem = file
        .name
        .get(file.name.len().saturating_sub(4)..)
        .filter(|extension| extension.eq_ignore_ascii_case(".txt"))
        .map_or(file.name.as_str(), |_| &file.name[..file.name.len() - 4]);
    let owner_text = if stem.eq_ignore_ascii_case("LoginCommandsGlobal") {
        None
    } else {
        let rest = stem
            .get(..PREFIX.len())
            .filter(|prefix| prefix.eq_ignore_ascii_case(PREFIX))
            .map(|_| &stem[PREFIX.len()..]);
        let Some(rest) = rest else {
            report.push(format!(
                "LoginCommands\\{}: the name does not say its account, server and character; \
                 skipped.",
                file.name
            ));
            return None;
        };
        Some(rest.to_owned())
    };
    let mut wait_ms = None;
    let mut count = None;
    let mut numbered = BTreeMap::new();
    for line in strip_bom(&file.text).lines() {
        let line = line.trim_end_matches('\r');
        let Some((key, value)) = line.split_once(':') else {
            continue;
        };
        let key = key.trim();
        if key.eq_ignore_ascii_case("WaitMilliseconds") {
            wait_ms = value.trim().parse::<u32>().ok();
        } else if key.eq_ignore_ascii_case("CommandCount") {
            count = value.trim().parse::<usize>().ok();
        } else if let Some(index) = key
            .get(.."Command".len())
            .filter(|prefix| prefix.eq_ignore_ascii_case("Command"))
            .and_then(|_| key["Command".len()..].parse::<usize>().ok())
        {
            numbered.insert(index, value.to_owned());
        }
    }
    let commands = numbered
        .into_iter()
        .filter(|(index, _)| count.is_none_or(|count| *index < count))
        .map(|(_, command)| command)
        .filter(|command| !command.trim().is_empty())
        .collect();
    Some(ThwargCommands {
        file_name: file.name.clone(),
        owner_text,
        owner: None,
        wait_ms,
        commands,
    })
}

/// Tells apart `<account>-<server>-<character>` when any of the three may
/// hold `-`: of every way to split it at two dashes, the one whose account
/// is in Accounts.txt and whose server is a server the files know (both
/// without regard to case). The account's index and the three parts; an
/// error when no split fits or more than one does.
fn split_owner(
    text: &str,
    accounts: &[ThwargAccount],
    known_servers: &BTreeSet<String>,
) -> Result<(usize, (String, String, String)), &'static str> {
    let dashes: Vec<usize> = text.match_indices('-').map(|(at, _)| at).collect();
    let mut fits = Vec::new();
    for (position, &first) in dashes.iter().enumerate() {
        for &second in &dashes[position + 1..] {
            let (account, server, character) = (
                &text[..first],
                &text[first + 1..second],
                &text[second + 1..],
            );
            if [account, server, character]
                .iter()
                .any(|part| part.trim().is_empty())
                || !known_servers.contains(&server.to_lowercase())
            {
                continue;
            }
            if let Some(index) = accounts
                .iter()
                .position(|known| same_account(&known.name, account))
            {
                let parts = (account.to_owned(), server.to_owned(), character.to_owned());
                fits.push((index, parts));
            }
        }
    }
    match fits.len() {
        1 => Ok(fits.remove(0)),
        0 => Err("its name holds no account from Accounts.txt and known server"),
        _ => Err("its name can be read as more than one account and server"),
    }
}

fn member_node<'a>(node: &'a Node, name: &str) -> Option<&'a Node> {
    host_json::object_members(node)
        .ok()?
        .iter()
        .find(|member| member.name == name)
        .map(|member| &member.value)
}

fn member_text(node: &Node, name: &str) -> Option<String> {
    host_json::string_or_null(member_node(node, name)?).ok()?
}

fn member_bool(node: &Node, name: &str) -> bool {
    member_node(node, name).is_some_and(|value| host_json::boolean(value).unwrap_or(false))
}

// --- The report --------------------------------------------------------------------------

/// What an import did, or would do. No line holds a password.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ThwargImportReport {
    /// Servers added, `Name (host:port)`.
    pub servers_added: Vec<String>,
    /// Thwarg servers matched to a launcher server.
    pub servers_matched: Vec<String>,
    /// Accounts added, with what they got.
    pub accounts_added: Vec<String>,
    /// Accounts already on their server, with what they gained.
    pub accounts_present: Vec<String>,
    /// Profile names added to the profile list.
    pub profiles_added: Vec<String>,
    /// What was left out, and why.
    pub skipped: Vec<String>,
    /// What else the player should know.
    pub notes: Vec<String>,
    /// How many servers and accounts were added or gained something.
    pub changes: usize,
}

impl ThwargImportReport {
    /// Whether the import changes anything.
    pub fn has_changes(&self) -> bool {
        self.changes > 0
    }

    /// One line: what an import will do (`applied` false) or did.
    pub fn summary(&self, applied: bool) -> String {
        let counts = format!(
            "{} {}, {} {}, {} already here, {} skipped",
            self.servers_added.len(),
            plural(self.servers_added.len(), "new server", "new servers"),
            self.accounts_added.len(),
            plural(self.accounts_added.len(), "new account", "new accounts"),
            self.accounts_present.len(),
            self.skipped.len(),
        );
        match (applied, self.has_changes()) {
            (true, true) => format!("Imported: {counts}."),
            (true, false) => format!("Nothing was imported: {counts}."),
            (false, true) => format!("Ready to import: {counts}. Press Import to add them."),
            (false, false) => format!("Nothing to import: {counts}."),
        }
    }

    /// The report as lines: a heading per part with something in it, then
    /// its entries indented.
    pub fn lines(&self, applied: bool) -> Vec<String> {
        let parts: [(&str, &str, &[String]); 7] = [
            ("Servers added", "Servers to add", &self.servers_added),
            (
                "Servers already here",
                "Servers already here",
                &self.servers_matched,
            ),
            ("Accounts added", "Accounts to add", &self.accounts_added),
            (
                "Accounts already here (only missing characters, and logon commands for \
                 characters without their own, are added)",
                "Accounts already here (only missing characters, and logon commands for \
                 characters without their own, are added)",
                &self.accounts_present,
            ),
            ("Profiles added", "Profiles to add", &self.profiles_added),
            ("Skipped", "Skipped", &self.skipped),
            ("Notes", "Notes", &self.notes),
        ];
        let mut lines = Vec::new();
        for (done, planned, entries) in parts {
            if entries.is_empty() {
                continue;
            }
            lines.push(if applied { done } else { planned }.to_owned());
            for entry in entries {
                for (index, line) in entry.lines().enumerate() {
                    let indent = if index == 0 { "  " } else { "      " };
                    lines.push(format!("{indent}{}", line.trim_start()));
                }
            }
        }
        lines
    }
}

fn plural<'a>(count: usize, one: &'a str, many: &'a str) -> &'a str {
    if count == 1 { one } else { many }
}

// --- The mapping -------------------------------------------------------------------------

/// What one account on one Thwarg server brings.
#[derive(Default)]
struct Reference {
    /// The server as the files spell it.
    server: String,
    characters: Vec<(String, Option<u32>)>,
    commands: Vec<ThwargCommands>,
}

/// What the import would do to `document`, which is left as it is.
pub fn preview(files: &ThwargFiles, document: &LauncherProfileDocument) -> ThwargImportReport {
    let mut copy = document.clone();
    import(files, &mut copy)
}

/// Merges the Thwarg launcher's files into `document` and reports what
/// changed. Nothing in the document is removed or overwritten; the caller
/// validates and saves it.
pub fn import(files: &ThwargFiles, document: &mut LauncherProfileDocument) -> ThwargImportReport {
    let mut report = ThwargImportReport::default();
    for line in &files.unreadable {
        report.skipped.push(format!("{line}; not read."));
    }
    let Some(accounts_text) = files.accounts.as_deref() else {
        report
            .skipped
            .push("Accounts.txt was not found, so there are no accounts to import.".into());
        return report;
    };
    let accounts = parse_accounts(accounts_text, &mut report.skipped);

    // Thwarg's own list wins over the published one for the same name.
    let mut servers = Vec::new();
    for (text, file) in [
        (&files.user_servers, r"Servers\UserServerList.xml"),
        (
            &files.published_servers,
            r"Servers\PublishedWildWestServerList.xml",
        ),
    ] {
        if let Some(text) = text {
            servers.extend(parse_server_list(text, file, &mut report.skipped));
        }
    }

    let mut profiles: Vec<ThwargProfile> = files
        .profiles
        .iter()
        .filter_map(|file| parse_profile(file, &mut report.skipped))
        .collect();
    profiles.sort_by(|a, b| a.file_name.cmp(&b.file_name));
    // Thwarg keeps its "last profile" setting outside its data folder; the
    // profile activated last is the one it opens with.
    let last = profiles
        .iter()
        .filter(|profile| profile.last_activated.is_some())
        .max_by(|a, b| {
            a.last_activated
                .cmp(&b.last_activated)
                .then_with(|| b.file_name.cmp(&a.file_name))
        });

    let mut command_files: Vec<ThwargCommands> = files
        .login_commands
        .iter()
        .filter_map(|file| parse_commands(file, &mut report.skipped))
        .collect();
    command_files.sort_by(|a, b| a.file_name.cmp(&b.file_name));
    let waits: BTreeSet<u32> = command_files
        .iter()
        .filter(|commands| !commands.commands.is_empty())
        .filter_map(|commands| commands.wait_ms)
        .collect();

    // Every (server, account) the files use.
    let mut references: BTreeMap<(String, usize), Reference> = BTreeMap::new();
    let mut unknown_accounts = BTreeSet::new();
    let find = |name: &str, unknown: &mut BTreeSet<String>| {
        let found = accounts
            .iter()
            .position(|account| same_account(&account.name, name));
        if found.is_none() {
            unknown.insert(name.to_lowercase());
        }
        found
    };
    let character_lists: Vec<ThwargCharacters> = files
        .characters
        .iter()
        .flat_map(|file| parse_characters(file, &mut report.skipped))
        .collect();
    // Every server name the files know, to tell the parts of a logon
    // command file's name apart.
    let known_servers: BTreeSet<String> = servers
        .iter()
        .map(|server| server.name.to_lowercase())
        .chain(
            character_lists
                .iter()
                .map(|list| list.server.to_lowercase()),
        )
        .chain(
            profiles
                .iter()
                .flat_map(|profile| &profile.settings)
                .map(|setting| setting.server.to_lowercase()),
        )
        .collect();
    for list in character_lists {
        let Some(account) = find(&list.account, &mut unknown_accounts) else {
            continue;
        };
        let entry = use_of(&mut references, &list.server, account);
        for (name, id) in list.characters {
            let duplicate = entry
                .characters
                .iter()
                .any(|(known, known_id)| *known == name || (id.is_some() && *known_id == id));
            if !duplicate {
                entry.characters.push((name, id));
            }
        }
    }
    let mut global = Vec::new();
    for mut commands in command_files {
        let Some(owner_text) = commands.owner_text.clone() else {
            global.push(commands);
            continue;
        };
        match split_owner(&owner_text, &accounts, &known_servers) {
            Ok((account, owner)) => {
                let server = owner.1.clone();
                commands.owner = Some(owner);
                use_of(&mut references, &server, account)
                    .commands
                    .push(commands);
            }
            Err(why) => report.skipped.push(format!(
                "LoginCommands\\{}: {why}; skipped.",
                commands.file_name
            )),
        }
    }
    for profile in &profiles {
        for setting in &profile.settings {
            // A profile lists every account on every server it knows; only
            // an active account or a picked character is a use.
            if !setting.active && setting.chosen.is_none() {
                continue;
            }
            if let Some(account) = find(&setting.account, &mut unknown_accounts) {
                use_of(&mut references, &setting.server, account);
            }
        }
    }
    for name in &unknown_accounts {
        report.skipped.push(format!(
            "{name}: used in the Thwarg files but not in Accounts.txt, so there is no password \
             to import."
        ));
    }
    for (index, account) in accounts.iter().enumerate() {
        if !references.keys().any(|(_, used)| *used == index) {
            report.skipped.push(format!(
                "{}: not used with any server in the Thwarg files.",
                account.name
            ));
        }
    }

    // The servers, then each account on them.
    let mut resolved: BTreeMap<String, Result<usize, String>> = BTreeMap::new();
    for ((server_key, account_index), reference) in references {
        let account = &accounts[account_index];
        let server_index = resolved
            .entry(server_key)
            .or_insert_with(|| resolve_server(&reference.server, &servers, document, &mut report))
            .clone();
        match server_index {
            Ok(index) => merge_account(
                &mut document.servers[index],
                account,
                &reference,
                &profiles,
                last,
                &mut report,
            ),
            Err(why) => report
                .skipped
                .push(format!("{} on {}: {why}.", account.name, reference.server)),
        }
    }

    // Every Thwarg profile is a profile here, even one with nothing in it.
    for profile in &profiles {
        if validate_profile_tags(std::slice::from_ref(&profile.name)).is_err() {
            report.skipped.push(format!(
                "Profiles\\{}: the Thwarg profile '{}' cannot be a profile name here (no , ; = \
                 \" or #), so nothing was put in it.",
                profile.file_name, profile.name
            ));
            continue;
        }
        let known = |list: &[String]| {
            list.iter()
                .any(|known| known.eq_ignore_ascii_case(&profile.name))
        };
        if !known(&document.profiles) {
            document.profiles.push(profile.name.clone());
            report.profiles_added.push(profile.name.clone());
            report.changes += 1;
        }
    }
    if let Some(last) = last {
        report.notes.push(format!(
            "Character choices come from the Thwarg profile '{}', the one activated last.",
            last.name
        ));
    }
    if !waits.is_empty() {
        let waits: Vec<String> = waits.iter().map(|ms| format!("{ms} ms")).collect();
        report.notes.push(format!(
            "Thwarg's logon command wait (WaitMilliseconds) was {}; this launcher has one delay \
             of {} ms between logon commands for every account, so it was not imported.",
            waits.join(", "),
            ac_host_core::config::DEFAULT_LOGIN_COMMAND_DELAY_MS
        ));
    }
    for commands in global
        .iter()
        .filter(|commands| !commands.commands.is_empty())
    {
        let mut line = format!(
            "Thwarg's global logon commands ({}) were not imported; add them to an account's \
             logon commands to keep them:",
            commands.file_name
        );
        for command in &commands.commands {
            line.push_str("\n    ");
            line.push_str(command);
        }
        report.notes.push(line);
    }
    report
}

/// The entry for `account` on the Thwarg server `server`.
fn use_of<'a>(
    references: &'a mut BTreeMap<(String, usize), Reference>,
    server: &str,
    account: usize,
) -> &'a mut Reference {
    let entry = references
        .entry((server.to_lowercase(), account))
        .or_default();
    if entry.server.is_empty() {
        server.clone_into(&mut entry.server);
    }
    entry
}

/// The launcher server for the Thwarg server `name`: one with its name;
/// else, from Thwarg's server lists, one with its host and port, or a new
/// one; or why its accounts are skipped.
fn resolve_server(
    name: &str,
    servers: &[ThwargServer],
    document: &mut LauncherProfileDocument,
    report: &mut ThwargImportReport,
) -> Result<usize, String> {
    let same_name = |known: &ServerProfile| known.name.to_uppercase() == name.to_uppercase();
    if let Some(index) = document.servers.iter().position(same_name) {
        let known = &document.servers[index];
        report
            .servers_matched
            .push(format!("{} ({}:{})", known.name, known.host, known.port));
        return Ok(index);
    }
    let server = servers
        .iter()
        .find(|server| server.name.eq_ignore_ascii_case(name))
        .ok_or_else(|| "that server is in neither of Thwarg's server lists".to_owned())?;
    let (host, port) = server
        .endpoint
        .clone()
        .map_err(|why| format!("Thwarg's server list entry is unusable: {why}"))?;
    let existing = document
        .servers
        .iter()
        .position(|known| known.host.eq_ignore_ascii_case(&host) && known.port == port);
    if let Some(index) = existing {
        let known = &document.servers[index];
        report.servers_matched.push(format!(
            "{} is {} here ({}:{})",
            server.name, known.name, known.host, known.port
        ));
        return Ok(index);
    }
    // Only the imported accounts go on it, not every saved login.
    let mut added = ServerProfile::new(server.name.clone(), host.clone(), port);
    added.account_list_initialized = true;
    document.servers.push(added);
    report
        .servers_added
        .push(format!("{} ({host}:{port})", server.name));
    report.changes += 1;
    Ok(document.servers.len() - 1)
}

fn merge_account(
    server: &mut ServerProfile,
    account: &ThwargAccount,
    reference: &Reference,
    profiles: &[ThwargProfile],
    last: Option<&ThwargProfile>,
    report: &mut ThwargImportReport,
) {
    let thwarg_server = reference.server.as_str();
    // `Some(None)`: the last profile sends this account to character select.
    let chosen = last
        .and_then(|profile| profile.setting(&account.name, thwarg_server))
        .map(|setting| setting.chosen.clone());
    let existing = server
        .accounts
        .iter()
        .position(|known| same_account(&known.account, &account.name));
    let Some(index) = existing else {
        let mut profile = AccountProfile::new(account.name.clone(), account.password.clone());
        add_characters(&mut profile, &reference.characters);
        let commands = give_commands(&mut profile, reference);
        for thwarg in profiles {
            if !thwarg.is_active(&account.name, thwarg_server)
                || validate_profile_tags(std::slice::from_ref(&thwarg.name)).is_err()
            {
                continue;
            }
            let picked = thwarg
                .setting(&account.name, thwarg_server)
                .and_then(|setting| setting.chosen.as_deref());
            tag(&mut profile, &thwarg.name, picked, &server.name, report);
        }
        match chosen {
            Some(None) => {
                profile.selected_launch_mode = Some(LaunchMode::GuiSelect);
            }
            Some(Some(name)) if profile.characters.iter().any(|known| known.name == name) => {
                profile.selected_character = Some(name);
                profile.selected_launch_mode = Some(LaunchMode::Gui);
            }
            _ => {}
        }
        report
            .accounts_added
            .push(describe_added(&server.name, &profile, &commands.given));
        report.changes += 1;
        server.accounts.push(profile);
        return;
    };
    let profile = &mut server.accounts[index];
    let added = add_characters(profile, &reference.characters);
    let commands = give_commands(profile, reference);
    let mut gained = Vec::new();
    if !added.is_empty() {
        gained.push(format!(
            "{} {} ({})",
            added.len(),
            plural(added.len(), "character", "characters"),
            added.join(", ")
        ));
    }
    if !commands.given.is_empty() {
        gained.push(describe_commands(&commands.given));
    }
    for character in &commands.kept {
        report.skipped.push(format!(
            "{} on {}: {character} keeps its own logon commands; Thwarg's were not imported.",
            profile.account, server.name
        ));
    }
    if gained.is_empty() {
        report.accounts_present.push(format!(
            "{} on {}: nothing to add",
            profile.account, server.name
        ));
    } else {
        report.changes += 1;
        report.accounts_present.push(format!(
            "{} on {}: adds {}",
            profile.account,
            server.name,
            gained.join(", ")
        ));
    }
}

/// Puts a Thwarg profile on a new account: on the character it picks for
/// the account, or on the account when it picks the character screen (or
/// a character the account does not have, which the report says). Within
/// an account a profile is on one character, and a name already there in
/// another case is left as it is.
fn tag(
    profile: &mut AccountProfile,
    name: &str,
    picked: Option<&str>,
    server_name: &str,
    report: &mut ThwargImportReport,
) {
    let carried = profile
        .profiles
        .iter()
        .any(|tag| tag.eq_ignore_ascii_case(name))
        || profile
            .characters
            .iter()
            .any(|character| character.has_profile(name));
    if carried {
        return;
    }
    let character = picked.and_then(|picked| {
        profile
            .characters
            .iter_mut()
            .find(|character| character.name == picked)
    });
    match (character, picked) {
        (Some(character), _) => character.profiles.push(name.to_owned()),
        (None, picked) => {
            if let Some(picked) = picked {
                report.notes.push(format!(
                    "{} on {server_name}: the Thwarg profile '{name}' picks {picked}, who is not \
                     among the account's characters, so the account is in the profile instead.",
                    profile.account
                ));
            }
            profile.profiles.push(name.to_owned());
        }
    }
}

/// Adds the characters `profile` lacks (by name, or by id); their names.
fn add_characters(
    profile: &mut AccountProfile,
    characters: &[(String, Option<u32>)],
) -> Vec<String> {
    let mut added = Vec::new();
    for (name, id) in characters {
        let known = profile.characters.iter().any(|known| {
            known.name == *name
                || (id.is_some() && character_id::try_parse(known.id.as_deref()) == *id)
        });
        if known {
            continue;
        }
        let mut character = CharacterProfile::new(name.clone());
        character.id = id.map(character_id::to_hex_string);
        profile.characters.push(character);
        added.push(name.clone());
    }
    added
}

/// Which characters got Thwarg's logon commands, and which kept their own.
#[derive(Default)]
struct GivenCommands {
    given: Vec<String>,
    kept: Vec<String>,
}

/// Gives each character its own Thwarg logon commands, as Thwarg keeps
/// them, when it has none of its own (a character named only by a command
/// file is added); the account's shared commands are left alone.
fn give_commands(profile: &mut AccountProfile, reference: &Reference) -> GivenCommands {
    let mut result = GivenCommands::default();
    // `reference.commands` is in file name order.
    for commands in reference
        .commands
        .iter()
        .filter(|commands| !commands.commands.is_empty())
    {
        let name = commands.character();
        let index = profile
            .characters
            .iter()
            .position(|character| character.name == name)
            .or_else(|| {
                profile
                    .characters
                    .iter()
                    .position(|character| character.name.eq_ignore_ascii_case(name))
            })
            .unwrap_or_else(|| {
                profile.characters.push(CharacterProfile::new(name));
                profile.characters.len() - 1
            });
        let character = &mut profile.characters[index];
        if character.login_commands.is_empty() {
            character.login_commands.clone_from(&commands.commands);
            result.given.push(character.name.clone());
        } else {
            result.kept.push(character.name.clone());
        }
    }
    result
}

/// "logon commands for Ann, Bea".
fn describe_commands(characters: &[String]) -> String {
    format!("logon commands for {}", characters.join(", "))
}

fn describe_added(server_name: &str, profile: &AccountProfile, commands: &[String]) -> String {
    let mut parts = vec![format!(
        "{} {}",
        profile.characters.len(),
        plural(profile.characters.len(), "character", "characters")
    )];
    if !commands.is_empty() {
        parts.push(describe_commands(commands));
    }
    if !profile.profiles.is_empty() {
        parts.push(format!("profiles {}", profile.profiles.join(", ")));
    }
    for character in profile
        .characters
        .iter()
        .filter(|character| !character.profiles.is_empty())
    {
        parts.push(format!(
            "{} in profiles {}",
            character.name,
            character.profiles.join(", ")
        ));
    }
    match (&profile.selected_character, profile.selected_launch_mode) {
        (Some(character), _) => parts.push(format!("launches {character}")),
        (None, Some(LaunchMode::GuiSelect)) => parts.push("launches to character select".into()),
        _ => {}
    }
    format!("{} on {server_name}: {}", profile.account, parts.join(", "))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::profiles::store::validate_and_normalize_document;

    const BOM: &str = "\u{feff}";
    const PASSWORDS: [&str; 5] = ["s3cret,x=1", "hunter9", "qpass", "ipass", "fpass"];

    fn accounts_text() -> String {
        format!(
            "{BOM}# Name=xxx,Password=xxx,LaunchPath=c:\\xxx,PreferencePath=c:\\xxx,Alias=xxx\r\n\
             Version=2\r\n\
             Name=Fernwick,Password=s3cret,x=1\r\n\
             Name=Brambleton,Password=hunter9\r\n\
             Name=Quillon,Password=qpass,Alias=Q\r\n\
             Name=Idlewood,Password=ipass\r\n\
             Name=Farshore,Password=fpass\r\n"
        )
    }

    fn server_item(name: &str, connect: &str) -> String {
        format!(
            "<ServerItem><id>00000000-0000-0000-0000-000000000000</id><name>{name}</name>\
             <alias /><description>An invented server</description><emu>ACE</emu>\
             <connect_string>{connect}</connect_string><discord_url /><website_url />\
             <GameApiUrlKey /><LoginServerUrlKey /><enable_login>true</enable_login>\
             <emu>ACE</emu><default_rodat>Off</default_rodat><default_secure>Off</default_secure>\
             <visibility>Visible</visibility></ServerItem>"
        )
    }

    fn server_list(items: &[(&str, &str)]) -> String {
        let items: String = items
            .iter()
            .map(|(name, connect)| server_item(name, connect))
            .collect();
        format!(
            "{BOM}<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<ArrayOfServerItem>{items}\
             </ArrayOfServerItem>"
        )
    }

    fn characters_file(server: &str, account: &str, characters: &[(u32, &str)]) -> ThwargFile {
        let list: Vec<String> = characters
            .iter()
            .map(|(id, name)| {
                format!("{{ \"Id\": {id}, \"Name\": \"{name}\", \"DeleteTimeout\": \"00:00:00\" }}")
            })
            .collect();
        ThwargFile::new(
            format!("characters_{server}_{account}.txt"),
            format!(
                "{{\r\n  \"{server}-{account}\": {{\r\n    \"ZoneId\": \"{}\",\r\n    \
                 \"CharacterList\": [{}]\r\n  }}\r\n}}",
                account.to_lowercase(),
                list.join(", ")
            ),
        )
    }

    fn commands_text(commands: &[&str]) -> String {
        let mut text = format!(
            "WaitMilliseconds:9999\r\nCommandCount:{}\r\n",
            commands.len()
        );
        for (index, command) in commands.iter().enumerate() {
            text.push_str(&format!("Command{index}:{command}\r\n"));
        }
        text
    }

    fn commands_file(
        account: &str,
        server: &str,
        character: &str,
        commands: &[&str],
    ) -> ThwargFile {
        ThwargFile::new(
            format!("LoginCommands-{account}-{server}-{character}.txt"),
            commands_text(commands),
        )
    }

    /// (account, active, chosen, server).
    fn profile_file(
        name: &str,
        activated: &str,
        states: &[(&str, bool)],
        settings: &[(&str, bool, &str, &str)],
    ) -> ThwargFile {
        let states: Vec<String> = states
            .iter()
            .map(|(account, active)| {
                format!("{{\"AccountName\":\"{account}\",\"Active\":{active}}}")
            })
            .collect();
        let settings: Vec<String> = settings
            .iter()
            .map(|(account, active, chosen, server)| {
                format!(
                    "{{\"AccountName\":\"{account}\",\"Active\":{active},\
                     \"ChosenCharacter\":\"{chosen}\",\"ServerName\":\"{server}\"}}"
                )
            })
            .collect();
        ThwargFile::new(
            format!("{}.txt", name.to_lowercase()),
            format!(
                "{BOM}{{\"AccountStates\":[{}],\"CharacterSettings\":[{}],\"Description\":\"\",\
                 \"FileVersion\":\"VER-3.0\",\"LastActivatedDate\":\"{activated}\",\
                 \"LastLaunchedDate\":\"\\/Date(1500000000000)\\/\",\
                 \"LastSavedDate\":\"\\/Date(1500000000000)\\/\",\"Name\":\"{name}\"}}",
                states.join(","),
                settings.join(",")
            ),
        )
    }

    fn files() -> ThwargFiles {
        ThwargFiles {
            accounts: Some(accounts_text()),
            user_servers: Some(server_list(&[("Hearthfall", "hearth.example.test:9000")])),
            published_servers: Some(server_list(&[
                ("Hearthfall", "other.example.test:9100"),
                ("Mistvale", "mist.example.test:9050"),
            ])),
            characters: vec![
                characters_file(
                    "Hearthfall",
                    "Fernwick",
                    &[(0x5000_0001, "Ash Vale-Runner"), (0x5000_0002, "Brindle")],
                ),
                characters_file(
                    "Mistvale",
                    "Brambleton",
                    &[(0x5000_0003, "Old Thorn"), (0x5000_0004, "New Thorn")],
                ),
                characters_file("Lostshore", "Farshore", &[(5, "Drift")]),
            ],
            profiles: vec![
                profile_file(
                    "Main",
                    "\\/Date(1700000000000)\\/",
                    &[("Fernwick", true), ("Quillon", false)],
                    &[
                        ("Fernwick", true, "Brindle", "Hearthfall"),
                        ("Quillon", true, "None", "Hearthfall"),
                        ("Idlewood", false, "None", "Hearthfall"),
                        ("Fernwick", false, "None", "Mistvale"),
                    ],
                ),
                profile_file(
                    "Bots",
                    "\\/Date(1600000000000+0100)\\/",
                    &[("Quillon", true)],
                    &[
                        ("Fernwick", false, "Ash Vale-Runner", "Hearthfall"),
                        ("Quillon", true, "None", "Hearthfall"),
                    ],
                ),
            ],
            login_commands: vec![
                commands_file(
                    "Fernwick",
                    "Hearthfall",
                    "Ash Vale-Runner",
                    &["/say hello", "/hop"],
                ),
                commands_file("Fernwick", "Hearthfall", "Brindle", &["/wave"]),
                commands_file("Brambleton", "Mistvale", "Old Thorn", &["/theirs"]),
                ThwargFile::new("LoginCommandsGlobal.txt", commands_text(&["/everyone"])),
            ],
            unreadable: Vec::new(),
        }
    }

    fn existing() -> LauncherProfileDocument {
        let mut account = AccountProfile::new("brambleton", "keep");
        account.login_commands = vec!["/shared".into()];
        let mut thorn = CharacterProfile::new("Old Thorn");
        thorn.id = Some("0x50000003".into());
        thorn.login_commands = vec!["/own".into()];
        account.characters.push(thorn);
        let mut mistvale = ServerProfile::new("Mistvale", "mist.example.test", 9050);
        mistvale.accounts.push(account);
        mistvale.account_list_initialized = true;
        let mut elsewhere = ServerProfile::new("Elsewhere", "elsewhere.example.test", 9000);
        elsewhere.account_list_initialized = true;
        LauncherProfileDocument {
            servers: vec![mistvale, elsewhere],
            ..LauncherProfileDocument::default()
        }
    }

    fn account<'a>(
        document: &'a LauncherProfileDocument,
        server: &str,
        name: &str,
    ) -> &'a AccountProfile {
        document
            .servers
            .iter()
            .find(|known| known.name == server)
            .and_then(|known| known.accounts.iter().find(|item| item.account == name))
            .unwrap_or_else(|| panic!("{name} on {server}"))
    }

    fn character_names(account: &AccountProfile) -> Vec<&str> {
        account
            .characters
            .iter()
            .map(|character| character.name.as_str())
            .collect()
    }

    // --- Parsers ---

    #[test]
    fn accounts_keep_a_password_with_a_comma_and_skip_comments_and_the_version() {
        let mut report = Vec::new();
        let accounts = parse_accounts(&accounts_text(), &mut report);
        let pairs: Vec<(&str, &str)> = accounts
            .iter()
            .map(|account| (account.name.as_str(), account.password.as_str()))
            .collect();
        assert_eq!(
            pairs,
            [
                ("Fernwick", "s3cret,x=1"),
                ("Brambleton", "hunter9"),
                ("Quillon", "qpass"),
                ("Idlewood", "ipass"),
                ("Farshore", "fpass"),
            ]
        );
        assert!(report.is_empty(), "{report:?}");
    }

    #[test]
    fn an_account_line_ends_its_password_before_a_known_key_and_names_before_any_key() {
        let fields = account_fields(r"Name=Ab,Password=x,y,Alias=Z,LaunchPath=c:\a,b");
        assert_eq!(
            fields,
            [
                ("Name", "Ab"),
                ("Password", "x,y"),
                ("Alias", "Z"),
                ("LaunchPath", r"c:\a,b"),
            ]
        );
        let fields = account_fields("Name=Ab,Extra=1,Password=p=q");
        assert_eq!(
            fields,
            [("Name", "Ab"), ("Extra", "1"), ("Password", "p=q")]
        );
    }

    #[test]
    fn a_repeated_or_nameless_account_line_is_reported_without_its_text() {
        let mut report = Vec::new();
        let accounts = parse_accounts(
            "Name=One,Password=first\nName=ONE,Password=second\nPassword=orphan\n",
            &mut report,
        );
        assert_eq!(accounts.len(), 1);
        assert_eq!(accounts[0].password, "first");
        assert_eq!(report.len(), 2, "{report:?}");
        assert!(
            report
                .iter()
                .all(|line| !line.contains("second") && !line.contains("orphan"))
        );
        assert!(!format!("{accounts:?}").contains("first"));
    }

    #[test]
    fn a_name_that_may_hold_the_password_is_skipped_by_line_number() {
        let mut report = Vec::new();
        let accounts = parse_accounts(
            "Name=Ok,Password=fine\nName=bob;Password=leaky1\nName=eve=x,Password=leaky2\n",
            &mut report,
        );
        assert_eq!(accounts.len(), 1);
        assert_eq!(
            report,
            [
                "Accounts.txt line 2: the account name is not readable; the line was skipped.",
                "Accounts.txt line 3: the account name is not readable; the line was skipped.",
            ]
        );
    }

    #[test]
    fn server_lists_read_with_a_byte_order_mark_and_two_emu_elements() {
        let mut report = Vec::new();
        let servers = parse_server_list(
            &server_list(&[
                ("Hearthfall", "hearth.example.test:9000"),
                ("Broken", "nohost"),
            ]),
            "list",
            &mut report,
        );
        assert_eq!(
            servers,
            [
                ThwargServer {
                    name: "Hearthfall".into(),
                    endpoint: Ok(("hearth.example.test".into(), 9000)),
                },
                ThwargServer {
                    name: "Broken".into(),
                    endpoint: Err("its address 'nohost' is not host:port".into()),
                },
            ]
        );
        assert!(report.is_empty());
        assert_eq!(parse_connect_string(" "), Err("it has no address".into()));
        assert!(parse_connect_string("h:0").is_err());
        assert!(parse_connect_string("h:70000").is_err());
        assert_eq!(
            parse_connect_string(" [::1]:9000 "),
            Ok(("[::1]".into(), 9000))
        );
    }

    #[test]
    fn a_characters_file_takes_its_server_and_account_from_the_key_and_zone() {
        let file = characters_file(
            "Under_Score-Server",
            "some_one",
            &[(0x5000_0001, "Ash Vale-Runner"), (0, "No Id")],
        );
        let mut report = Vec::new();
        let lists = parse_characters(&file, &mut report);
        assert_eq!(
            lists,
            [ThwargCharacters {
                server: "Under_Score-Server".into(),
                account: "some_one".into(),
                characters: vec![
                    ("Ash Vale-Runner".into(), Some(0x5000_0001)),
                    ("No Id".into(), None),
                ],
            }]
        );
        let accented = ThwargFile::new(
            "characters_Vale_Ärlig.txt",
            r#"{"Vale-Ärlig": {"ZoneId": "ärlig", "CharacterList": []}}"#,
        );
        let lists = parse_characters(&accented, &mut report);
        assert_eq!(
            (lists[0].server.as_str(), lists[0].account.as_str()),
            ("Vale", "Ärlig")
        );
        let bad = ThwargFile::new(
            "characters_x_y.txt",
            r#"{"x-y": {"ZoneId": "other", "CharacterList": []}}"#,
        );
        assert!(parse_characters(&bad, &mut report).is_empty());
        assert_eq!(report.len(), 1, "{report:?}");
    }

    #[test]
    fn a_profile_reads_with_a_byte_order_mark_and_none_is_the_character_screen() {
        let mut report = Vec::new();
        let profile = parse_profile(&files().profiles[0], &mut report).unwrap();
        assert_eq!(profile.name, "Main");
        assert_eq!(profile.last_activated, Some(1_700_000_000_000));
        assert_eq!(
            profile
                .setting("fernwick", "HEARTHFALL")
                .unwrap()
                .chosen
                .as_deref(),
            Some("Brindle")
        );
        assert_eq!(
            profile.setting("Quillon", "Hearthfall").unwrap().chosen,
            None
        );
        // Account state and setting both ticked.
        assert!(profile.is_active("Fernwick", "Hearthfall"));
        // The setting is ticked but the account is not in the profile.
        assert!(!profile.is_active("Quillon", "Hearthfall"));
        assert!(!profile.is_active("Idlewood", "Hearthfall"));
        // In the profile, but no setting on that server.
        assert!(!profile.is_active("Fernwick", "Elsewhere"));
        assert!(report.is_empty());
        assert_eq!(
            parse_json_date("/Date(1600000000000+0100)/"),
            Some(1_600_000_000_000)
        );
        assert_eq!(parse_json_date("/Date(-5)/"), Some(-5));
        assert_eq!(parse_json_date("soon"), None);
    }

    #[test]
    fn a_command_file_name_splits_account_and_server_and_keeps_a_dashed_character() {
        let mut report = Vec::new();
        let file = commands_file("Fernwick", "Hearthfall", "Ash Vale-Runner", &["/a", "/b:c"]);
        let commands = parse_commands(&file, &mut report).unwrap();
        assert_eq!(
            commands.owner_text.as_deref(),
            Some("Fernwick-Hearthfall-Ash Vale-Runner")
        );
        assert_eq!(commands.commands, ["/a", "/b:c"]);
        assert_eq!(commands.wait_ms, Some(9999));
        // Only the first CommandCount commands count.
        let short = ThwargFile::new(
            "LoginCommands-a-b-c.txt",
            "CommandCount:1\nCommand0:/kept\nCommand1:/stale\n",
        );
        assert_eq!(
            parse_commands(&short, &mut report).unwrap().commands,
            ["/kept"]
        );
        let global = ThwargFile::new("LoginCommandsGlobal.txt", commands_text(&["/g"]));
        assert_eq!(
            parse_commands(&global, &mut report).unwrap().owner_text,
            None
        );
        let bad = ThwargFile::new("LoginCommandsOther.txt", commands_text(&["/x"]));
        assert!(parse_commands(&bad, &mut report).is_none());
        assert_eq!(
            report,
            [
                r"LoginCommands\LoginCommandsOther.txt: the name does not say its account, server and character; skipped."
            ]
        );
    }

    fn accounts_named(names: &[&str]) -> Vec<ThwargAccount> {
        names
            .iter()
            .map(|name| ThwargAccount {
                name: (*name).to_owned(),
                password: String::new(),
            })
            .collect()
    }

    fn servers_named(names: &[&str]) -> BTreeSet<String> {
        names.iter().map(|name| name.to_lowercase()).collect()
    }

    fn owner(account: &str, server: &str, character: &str) -> (String, String, String) {
        (account.into(), server.into(), character.into())
    }

    #[test]
    fn a_command_file_name_is_split_where_a_known_account_and_server_fit() {
        let accounts = accounts_named(&["bot", "dash-bot"]);
        // A server name with a dash.
        assert_eq!(
            split_owner(
                "bot-Reef-Cull-Ann",
                &accounts,
                &servers_named(&["Reef-Cull"])
            ),
            Ok((0, owner("bot", "Reef-Cull", "Ann")))
        );
        // An account name with a dash, matched without regard to case.
        assert_eq!(
            split_owner(
                "DASH-bot-Reef-Old-Ann",
                &accounts,
                &servers_named(&["reef"])
            ),
            Ok((1, owner("DASH-bot", "Reef", "Old-Ann")))
        );
        // Two readings fit: skipped.
        assert_eq!(
            split_owner(
                "bot-Reef-Cull-Ann",
                &accounts,
                &servers_named(&["Reef", "Reef-Cull"])
            ),
            Err("its name can be read as more than one account and server")
        );
        // None fits.
        assert_eq!(
            split_owner("ghost-Reef-Ann", &accounts, &servers_named(&["Reef"])),
            Err("its name holds no account from Accounts.txt and known server")
        );
        assert!(split_owner("nodash", &accounts, &servers_named(&["Reef"])).is_err());
    }

    #[test]
    fn the_import_files_commands_under_a_server_whose_name_has_a_dash() {
        let mut files = files();
        files.user_servers = Some(server_list(&[
            ("Hearthfall", "hearth.example.test:9000"),
            ("Reef-Cull", "reef.example.test:9000"),
        ]));
        files.login_commands = vec![
            commands_file("Fernwick", "Reef-Cull", "Ann", &["/reef"]),
            ThwargFile::new(
                "LoginCommands-Nobody-Reef-Cull-Ann.txt",
                commands_text(&["/x"]),
            ),
        ];
        let mut document = existing();
        let report = import(&files, &mut document);
        // The file's character is not in a character list: the import adds
        // it with its commands (changed with commands per character; the
        // account took them before).
        let fernwick = account(&document, "Reef-Cull", "Fernwick");
        assert!(fernwick.login_commands.is_empty());
        assert_eq!(character_names(fernwick), ["Ann"]);
        assert_eq!(fernwick.characters[0].login_commands, ["/reef"]);
        assert!(report.skipped.contains(
            &r"LoginCommands\LoginCommands-Nobody-Reef-Cull-Ann.txt: its name holds no account from Accounts.txt and known server; skipped."
                .to_owned()
        ));
    }

    // --- The mapping ---

    #[test]
    fn the_import_adds_servers_accounts_characters_and_tags_and_validates() {
        let mut document = existing();
        let report = import(&files(), &mut document);
        validate_and_normalize_document(&mut document).unwrap();

        assert_eq!(
            report.servers_added,
            ["Hearthfall (hearth.example.test:9000)"]
        );
        assert_eq!(
            report.servers_matched,
            ["Mistvale (mist.example.test:9050)"]
        );
        let hearthfall = document
            .servers
            .iter()
            .find(|server| server.name == "Hearthfall")
            .unwrap();
        assert_eq!(
            (hearthfall.host.as_str(), hearthfall.port),
            ("hearth.example.test", 9000)
        );
        let names: Vec<&str> = hearthfall
            .accounts
            .iter()
            .map(|account| account.account.as_str())
            .collect();
        assert_eq!(names, ["Fernwick", "Quillon"]);

        let fernwick = account(&document, "Hearthfall", "Fernwick");
        assert_eq!(fernwick.password, "s3cret,x=1");
        assert_eq!(character_names(fernwick), ["Ash Vale-Runner", "Brindle"]);
        assert_eq!(fernwick.characters[0].id.as_deref(), Some("0x50000001"));
        // Main picks Brindle for Fernwick, so the profile is on Brindle, not
        // on the account (changed with profiles on characters).
        assert!(fernwick.profiles.is_empty());
        assert_eq!(fernwick.characters[1].profiles, ["Main"]);
        assert!(fernwick.characters[0].profiles.is_empty());
        assert_eq!(fernwick.selected_character.as_deref(), Some("Brindle"));
        assert_eq!(fernwick.selected_launch_mode, Some(LaunchMode::Gui));

        let quillon = account(&document, "Hearthfall", "Quillon");
        assert!(quillon.characters.is_empty());
        // Main ticks Quillon's setting but not its account: Bots only, which
        // picks the character screen, so it is on the account.
        assert_eq!(quillon.profiles, ["Bots"]);
        assert_eq!(quillon.selected_character, None);
        assert_eq!(quillon.selected_launch_mode, Some(LaunchMode::GuiSelect));

        assert_eq!(document.profiles, ["Bots", "Main"]);
        assert_eq!(report.profiles_added, ["Bots", "Main"]);

        // Nothing lands on a server the Thwarg files do not use.
        let elsewhere = document
            .servers
            .iter()
            .find(|server| server.name == "Elsewhere")
            .unwrap();
        assert!(elsewhere.accounts.is_empty());
        // A server, two accounts, one account gaining a character, and the
        // two profiles.
        assert_eq!(report.changes, 6);
        assert!(report.has_changes());
        assert_eq!(
            report.accounts_added[0],
            "Fernwick on Hearthfall: 2 characters, logon commands for Ash Vale-Runner, Brindle, \
             Brindle in profiles Main, launches Brindle"
        );
    }

    #[test]
    fn an_existing_account_is_not_overwritten_and_only_gains_missing_characters() {
        let mut document = existing();
        let report = import(&files(), &mut document);
        let brambleton = account(&document, "Mistvale", "brambleton");
        assert_eq!(brambleton.password, "keep");
        assert_eq!(brambleton.login_commands, ["/shared"]);
        assert_eq!(character_names(brambleton), ["Old Thorn", "New Thorn"]);
        // Old Thorn has commands of its own, so Thwarg's are not imported.
        assert_eq!(brambleton.characters[0].login_commands, ["/own"]);
        assert!(brambleton.profiles.is_empty());
        assert_eq!(
            report.accounts_present,
            ["brambleton on Mistvale: adds 1 character (New Thorn)"]
        );
        assert!(report.skipped.contains(
            &"brambleton on Mistvale: Old Thorn keeps its own logon commands; Thwarg's were not \
              imported."
                .to_owned()
        ));
        assert_eq!(
            document.servers[0].accounts.len(),
            1,
            "no second spelling of the account"
        );
    }

    #[test]
    fn an_existing_character_without_commands_gets_thwargs_and_the_shared_ones_stay() {
        let mut document = existing();
        document.servers[0].accounts[0].characters[0]
            .login_commands
            .clear();
        let report = import(&files(), &mut document);
        let brambleton = account(&document, "Mistvale", "brambleton");
        assert_eq!(brambleton.characters[0].login_commands, ["/theirs"]);
        assert_eq!(brambleton.login_commands, ["/shared"]);
        assert_eq!(
            report.accounts_present,
            [
                "brambleton on Mistvale: adds 1 character (New Thorn), logon commands for Old \
                 Thorn"
            ]
        );
    }

    #[test]
    fn each_character_keeps_its_own_commands() {
        // Before commands per character, the account kept the chosen
        // character's list and the report listed the others as not kept.
        let mut document = existing();
        let report = import(&files(), &mut document);
        let fernwick = account(&document, "Hearthfall", "Fernwick");
        assert!(fernwick.login_commands.is_empty());
        assert_eq!(
            fernwick.characters[0].login_commands,
            ["/say hello", "/hop"]
        );
        assert_eq!(fernwick.characters[1].login_commands, ["/wave"]);
        assert!(!report.lines(true).join("\n").contains("not kept"));
    }

    #[test]
    fn without_profiles_the_characters_still_keep_their_commands() {
        let mut files = files();
        files.profiles.clear();
        let mut document = existing();
        let report = import(&files, &mut document);
        let fernwick = account(&document, "Hearthfall", "Fernwick");
        assert_eq!(
            fernwick.characters[0].login_commands,
            ["/say hello", "/hop"]
        );
        assert_eq!(fernwick.characters[1].login_commands, ["/wave"]);
        assert_eq!(fernwick.selected_character, None);
        assert_eq!(fernwick.selected_launch_mode, None);
        assert!(document.profiles.is_empty());
        assert!(report.profiles_added.is_empty());
    }

    #[test]
    fn a_profile_picking_a_character_the_account_lacks_goes_on_the_account() {
        let mut files = files();
        files.profiles[0] = profile_file(
            "Main",
            "\\/Date(1700000000000)\\/",
            &[("Fernwick", true)],
            &[("Fernwick", true, "Nobody Here", "Hearthfall")],
        );
        let mut document = existing();
        let report = import(&files, &mut document);
        let fernwick = account(&document, "Hearthfall", "Fernwick");
        assert_eq!(fernwick.profiles, ["Main"]);
        assert!(
            fernwick
                .characters
                .iter()
                .all(|character| character.profiles.is_empty())
        );
        assert!(report.notes.iter().any(|note| note.contains(
            "the Thwarg profile 'Main' picks Nobody Here, who is not among the account's \
             characters"
        )));
    }

    #[test]
    fn an_empty_profile_is_added_and_a_bad_name_is_skipped_once() {
        let mut files = files();
        files
            .profiles
            .push(profile_file("Spare", "\\/Date(1400000000000)\\/", &[], &[]));
        files.profiles.push(profile_file(
            "Bad,Name",
            "\\/Date(1400000000000)\\/",
            &[("Fernwick", true)],
            &[("Fernwick", true, "Brindle", "Hearthfall")],
        ));
        let mut document = existing();
        let report = import(&files, &mut document);
        validate_and_normalize_document(&mut document).unwrap();
        assert_eq!(document.profiles, ["Bots", "Main", "Spare"]);
        let skipped: Vec<&String> = report
            .skipped
            .iter()
            .filter(|line| line.contains("Bad,Name"))
            .collect();
        assert_eq!(
            skipped,
            [
                "Profiles\\bad,name.txt: the Thwarg profile 'Bad,Name' cannot be a profile name \
                 here (no , ; = \" or #), so nothing was put in it."
            ]
        );
        let brindle = &account(&document, "Hearthfall", "Fernwick").characters[1];
        assert_eq!(brindle.profiles, ["Main"]);
    }

    #[test]
    fn unusable_accounts_and_servers_are_skipped_with_why() {
        let report = preview(&files(), &existing());
        assert!(
            report.skipped.contains(
                &"Farshore on Lostshore: that server is in neither of Thwarg's server lists."
                    .to_owned()
            )
        );
        assert!(
            report
                .skipped
                .contains(&"Idlewood: not used with any server in the Thwarg files.".to_owned())
        );
        let notes = report.notes.join("\n");
        assert!(notes.contains("the Thwarg profile 'Main'"));
        assert!(notes.contains("WaitMilliseconds) was 9999 ms"));
        assert!(notes.contains("LoginCommandsGlobal.txt") && notes.contains("/everyone"));
    }

    #[test]
    fn the_user_server_list_wins_over_the_published_one() {
        let mut files = files();
        files.user_servers = None;
        let report = preview(&files, &existing());
        assert_eq!(
            report.servers_added,
            ["Hearthfall (other.example.test:9100)"]
        );
    }

    #[test]
    fn a_server_with_the_same_address_is_reused_under_its_own_name() {
        let mut document = existing();
        document.servers[1].host = "HEARTH.example.test".into();
        let report = import(&files(), &mut document);
        assert!(report.servers_added.is_empty());
        assert_eq!(
            account(&document, "Elsewhere", "Fernwick").password,
            "s3cret,x=1"
        );
    }

    #[test]
    fn a_launcher_server_of_that_name_is_used_without_a_thwarg_list_entry() {
        let mut document = existing();
        let mut lostshore = ServerProfile::new("LOSTSHORE", "lost.example.test", 9000);
        lostshore.account_list_initialized = true;
        document.servers.push(lostshore);
        let report = import(&files(), &mut document);
        let farshore = account(&document, "LOSTSHORE", "Farshore");
        assert_eq!(farshore.password, "fpass");
        assert_eq!(character_names(farshore), ["Drift"]);
        assert!(
            report
                .servers_matched
                .contains(&"LOSTSHORE (lost.example.test:9000)".to_owned())
        );
        assert!(!report.skipped.iter().any(|line| line.contains("Lostshore")));
    }

    #[test]
    fn a_second_import_adds_nothing() {
        let mut document = existing();
        import(&files(), &mut document);
        let before = document.clone();
        let report = import(&files(), &mut document);
        assert_eq!(document, before);
        assert!(!report.has_changes());
        assert!(report.summary(false).starts_with("Nothing to import"));
    }

    #[test]
    fn preview_leaves_the_document_alone() {
        let document = existing();
        let report = preview(&files(), &document);
        assert_eq!(document, existing());
        assert!(
            report
                .summary(false)
                .starts_with("Ready to import: 1 new server, 2 new accounts")
        );
    }

    #[test]
    fn no_password_appears_in_the_report() {
        let report = preview(&files(), &existing());
        let text = format!(
            "{}\n{}\n{report:?}\n{:?}",
            report.lines(false).join("\n"),
            report.summary(true),
            files()
        );
        for password in PASSWORDS {
            assert!(!text.contains(password), "{password}");
        }
    }

    #[test]
    fn missing_accounts_file_imports_nothing() {
        let mut files = files();
        files.accounts = None;
        let mut document = existing();
        let report = import(&files, &mut document);
        assert_eq!(document, existing());
        assert_eq!(report.skipped.len(), 1);
    }

    #[test]
    fn read_folder_reads_the_files_in_their_folders_without_the_byte_order_mark() {
        let root = std::env::temp_dir()
            .join("openac-thwarg-import-tests")
            .join(crate::ids::unique_hex32());
        let write = |relative: &str, text: &str| {
            let path = root.join(relative);
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            std::fs::write(path, text).unwrap();
        };
        write("Accounts.txt", &accounts_text());
        write("Servers/UserServerList.xml", "<a/>");
        write("characters/characters_A_b.txt", "{}");
        write("characters/notes.txt", "ignored");
        write("Profiles/main.txt", "{}");
        write("LoginCommands/LoginCommandsGlobal.txt", "");
        write("LoginCommands/other.txt", "ignored");
        std::fs::write(root.join("Profiles").join("bad.txt"), [0xFF, 0xFE]).unwrap();
        let files = read_folder(&root).unwrap();
        let _ = std::fs::remove_dir_all(&root);
        assert!(files.accounts.as_deref().unwrap().starts_with("# Name"));
        assert_eq!(files.user_servers.as_deref(), Some("<a/>"));
        assert_eq!(files.published_servers, None);
        let names = |list: &[ThwargFile]| -> Vec<String> {
            list.iter().map(|file| file.name.clone()).collect()
        };
        assert_eq!(names(&files.characters), ["characters_A_b.txt"]);
        assert_eq!(names(&files.profiles), ["main.txt"]);
        assert_eq!(names(&files.login_commands), ["LoginCommandsGlobal.txt"]);
        assert_eq!(files.unreadable, [r"Profiles\bad.txt: not UTF-8 text"]);
        assert!(read_folder(&root).is_err());
    }

    #[test]
    fn read_folder_skips_files_past_the_size_and_count_limits() {
        let root = std::env::temp_dir()
            .join("openac-thwarg-import-tests")
            .join(crate::ids::unique_hex32());
        std::fs::create_dir_all(root.join("Profiles")).unwrap();
        std::fs::write(root.join("Accounts.txt"), "Name=A,Password=12345").unwrap();
        for name in ["a.txt", "b.txt", "c.txt", "d.txt"] {
            std::fs::write(root.join("Profiles").join(name), "{}").unwrap();
        }
        let limits = Limits {
            file_bytes: 10,
            files_per_folder: 2,
        };
        let files = read_folder_within(&root, limits).unwrap();
        let _ = std::fs::remove_dir_all(&root);
        assert_eq!(files.accounts, None);
        let names: Vec<&str> = files
            .profiles
            .iter()
            .map(|file| file.name.as_str())
            .collect();
        assert_eq!(names, ["a.txt", "b.txt"]);
        assert_eq!(
            files.unreadable,
            [
                "Accounts.txt: larger than 10 bytes",
                r"Profiles: more than 2 files; 2 from c.txt on",
            ]
        );
    }
}
