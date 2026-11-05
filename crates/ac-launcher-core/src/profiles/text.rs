//! The plain-text views of the profile document the launcher's editors
//! show, and the parsers that write them back. A parse checks the whole
//! text first and changes nothing when any line is wrong; every error names
//! its line.
//!
//! Accounts, grouped by server:
//!
//! ```text
//! #Coldeve
//! Name=notan3,Password=secret,Profiles=Main;Bots
//! ```
//!
//! Logon commands, grouped by server and account:
//!
//! ```text
//! #Coldeve
//! ##notan3
//! /vt start
//! ```
//!
//! In both, a server's section is the whole truth for that server, and a
//! server left out of the text is left as it is. A logon command line
//! starting with a backslash is a command taken without it, which is how a
//! command starting with `#` is written. Servers are one `name | host |
//! port` line each.

use std::collections::HashSet;

use ac_host_core::json as host_json;

use super::account_name::{find_account, same_account};
use super::document::{AccountProfile, LauncherProfileDocument, ServerProfile};
use super::store::{ProfileError, copy_known_account_credentials};
use crate::json;

/// Which projection an editor shows.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LauncherTextEditorKind {
    /// Accounts by server: `Name=…,Password=…,Profiles=…` lines.
    Accounts,
    /// One `name | host | port` line per server.
    Servers,
    /// Logon commands by server and account.
    LogonCommands,
}

const SERVER_PREFIX: &str = "#";
const ACCOUNT_PREFIX: &str = "##";

/// Starts a logon command line that is taken as written, without the
/// backslash: how a command beginning with `#` or `\` is written.
const ESCAPE: char = '\\';

fn require(condition: bool, message: &str) -> Result<(), ProfileError> {
    if condition {
        Ok(())
    } else {
        Err(ProfileError::Invalid(message.to_owned()))
    }
}

/// The projection as text.
pub fn read_text(document: &LauncherProfileDocument, kind: LauncherTextEditorKind) -> String {
    match kind {
        LauncherTextEditorKind::Accounts => read_accounts(document),
        LauncherTextEditorKind::Servers => document
            .servers
            .iter()
            .map(|server| {
                format!(
                    "{} | {} | {}",
                    encode(&server.name),
                    encode(&server.host),
                    server.port
                )
            })
            .collect::<Vec<_>>()
            .join("\n"),
        LauncherTextEditorKind::LogonCommands => read_logon_commands(document),
    }
}

/// Applies edited text to the document; nothing changes when any line is
/// wrong.
pub fn apply_text(
    document: &mut LauncherProfileDocument,
    kind: LauncherTextEditorKind,
    text: &str,
) -> Result<(), ProfileError> {
    match kind {
        LauncherTextEditorKind::Accounts => apply_accounts(document, text),
        LauncherTextEditorKind::Servers => apply_servers(document, text),
        LauncherTextEditorKind::LogonCommands => apply_logon_commands(document, text),
    }
}

// --- Accounts ---------------------------------------------------------------

fn read_accounts(document: &LauncherProfileDocument) -> String {
    let mut text = String::new();
    for server in &document.servers {
        if !text.is_empty() {
            text.push('\n');
        }
        text.push_str(SERVER_PREFIX);
        text.push_str(&encode_header(&server.name));
        text.push('\n');
        for account in &server.accounts {
            text.push_str("Name=");
            text.push_str(&encode(&account.account));
            text.push_str(",Password=");
            text.push_str(&encode(&account.password));
            if !account.profiles.is_empty() {
                text.push_str(",Profiles=");
                text.push_str(&encode(&account.profiles.join(";")));
            }
            text.push('\n');
        }
    }
    text
}

struct ParsedAccount {
    name: String,
    password: String,
    profiles: Vec<String>,
}

/// The accounts saving `text` would remove that have something saved on
/// them (characters, plugins or logon commands), each as "account on
/// server (1 character, 2 plugins)". A changed name counts: it is a
/// different account, with its own characters; the same name in another
/// case is the same account. Empty when there are none, or when the text
/// has an error the save will report.
pub fn describe_account_removals(document: &LauncherProfileDocument, text: &str) -> Vec<String> {
    let mut errors = Vec::new();
    let sections = parse_accounts(document, text, &mut errors);
    if !errors.is_empty() {
        return Vec::new();
    }
    let count = |count: usize, noun: &str| match count {
        0 => None,
        1 => Some(format!("1 {noun}")),
        _ => Some(format!("{count} {noun}s")),
    };
    let mut removals = Vec::new();
    for (server_index, accounts) in sections {
        let server = &document.servers[server_index];
        for (_, account) in server.accounts.iter().enumerate().filter(|(index, _)| {
            accounts
                .iter()
                .all(|parsed| saved_entry(&server.accounts, &parsed.name) != Some(*index))
        }) {
            let parts: Vec<String> = [
                count(account.characters.len(), "character"),
                count(account.plugins.len(), "plugin"),
                count(account.login_commands.len(), "logon command"),
            ]
            .into_iter()
            .flatten()
            .collect();
            if !parts.is_empty() {
                removals.push(format!(
                    "{} on {} ({})",
                    account.account,
                    server.name,
                    parts.join(", ")
                ));
            }
        }
    }
    removals
}

fn apply_accounts(document: &mut LauncherProfileDocument, text: &str) -> Result<(), ProfileError> {
    let mut errors = Vec::new();
    let sections = parse_accounts(document, text, &mut errors);
    throw_if_any(&errors)?;
    for (server_index, accounts) in sections {
        let server = &mut document.servers[server_index];
        let old = std::mem::take(&mut server.accounts);
        server.accounts = accounts
            .into_iter()
            .map(|parsed| {
                // The same account in another case keeps everything and
                // takes the new spelling.
                let mut account = saved_entry(&old, &parsed.name)
                    .map(|index| old[index].clone())
                    .unwrap_or_else(|| AccountProfile::new(parsed.name.clone(), String::new()));
                parsed.name.clone_into(&mut account.account);
                account.password = parsed.password;
                account.profiles = parsed.profiles;
                account
            })
            .collect();
    }
    Ok(())
}

/// The saved account a parsed line stands for.
fn saved_entry(saved: &[AccountProfile], name: &str) -> Option<usize> {
    find_account(saved, |account| &account.account, name)
}

fn parse_accounts(
    document: &LauncherProfileDocument,
    text: &str,
    errors: &mut Vec<String>,
) -> Vec<(usize, Vec<ParsedAccount>)> {
    let mut sections: Vec<(usize, Vec<ParsedAccount>)> = Vec::new();
    // Under a server line that was wrong, `None`: its lines are skipped.
    let mut current: Option<usize> = None;
    let mut server_seen = false;
    for (number, line) in lines(text) {
        if let Some(header) = line.strip_prefix(SERVER_PREFIX) {
            server_seen = true;
            let mut server = resolve_server(document, header, number, errors);
            if let Some(index) = server
                && sections.iter().any(|(existing, _)| *existing == index)
            {
                errors.push(format!(
                    "Line {number}: server '{}' appears more than once.",
                    document.servers[index].name
                ));
                server = None;
            }
            current = server.map(|index| {
                sections.push((index, Vec::new()));
                sections.len() - 1
            });
            continue;
        }
        let Some(section) = current else {
            if !server_seen {
                errors.push(format!(
                    "Line {number}: put a #Server line above the accounts on that server."
                ));
            }
            continue;
        };
        if let Some(account) = try_parse_account_line(line, number, errors) {
            let (server_index, accounts) = &mut sections[section];
            // Two spellings of one account are a duplicate, unless both are
            // already saved: a file saved before names were compared
            // without case can hold them, and its text must still save.
            let saved = &document.servers[*server_index].accounts;
            let is_saved = |name: &str| saved.iter().any(|item| item.account == name);
            if accounts.iter().any(|existing| {
                existing.name == account.name
                    || (same_account(&existing.name, &account.name)
                        && !(is_saved(&existing.name) && is_saved(&account.name)))
            }) {
                errors.push(format!(
                    "Line {number}: account '{}' appears more than once on this server.",
                    account.name
                ));
                continue;
            }
            accounts.push(account);
        }
    }
    sections
}

fn try_parse_account_line(
    line: &str,
    number: usize,
    errors: &mut Vec<String>,
) -> Option<ParsedAccount> {
    let fields = match split_fields(line, ',') {
        Ok(fields) => fields,
        Err(error) => {
            errors.push(format!("Line {number}: {error}"));
            return None;
        }
    };
    let mut name: Option<String> = None;
    let mut password: Option<String> = None;
    let mut profiles: Vec<String> = Vec::new();
    for field in fields {
        let Some(equals) = field.find('=') else {
            errors.push(format!(
                "Line {number}: expected Name=…,Password=… (a server line starts with #)."
            ));
            return None;
        };
        let key = field[..equals].trim();
        let value = match decode(&field[equals + 1..]) {
            Ok(value) => value,
            Err(error) => {
                errors.push(format!("Line {number}: {error}"));
                return None;
            }
        };
        if key.eq_ignore_ascii_case("Name") && name.is_none() {
            name = Some(value);
        } else if key.eq_ignore_ascii_case("Password") && password.is_none() {
            password = Some(value);
        } else if key.eq_ignore_ascii_case("Profiles") && profiles.is_empty() {
            for tag in value
                .split(';')
                .map(str::trim)
                .filter(|tag| !tag.is_empty())
            {
                if tag.contains([',', '=', '"', '#', '\r', '\n', '\t']) {
                    errors.push(format!(
                        "Line {number}: '{tag}' is not a profile name. Use letters, digits and \
                         spaces."
                    ));
                    return None;
                }
                if profiles.iter().any(|known| known.eq_ignore_ascii_case(tag)) {
                    errors.push(format!("Line {number}: profile '{tag}' is listed twice."));
                    return None;
                }
                profiles.push(tag.to_owned());
            }
        } else {
            errors.push(format!(
                "Line {number}: '{key}' is not Name, Password or Profiles, or appears twice."
            ));
            return None;
        }
    }
    let Some(name) = name.filter(|name| !name.trim().is_empty()) else {
        errors.push(format!("Line {number}: each account needs Name=."));
        return None;
    };
    Some(ParsedAccount {
        name,
        password: password.unwrap_or_default(),
        profiles,
    })
}

// --- Logon commands -----------------------------------------------------------

fn read_logon_commands(document: &LauncherProfileDocument) -> String {
    let mut text = String::new();
    for server in &document.servers {
        if !text.is_empty() {
            text.push('\n');
        }
        text.push_str(SERVER_PREFIX);
        text.push_str(&encode_header(&server.name));
        text.push('\n');
        for account in &server.accounts {
            text.push_str(ACCOUNT_PREFIX);
            text.push_str(&encode_header(&account.account));
            text.push('\n');
            for command in &account.login_commands {
                // A command that would read as a # or ## line, or as an
                // escape, gets a backslash.
                if command.starts_with(SERVER_PREFIX) || command.starts_with(ESCAPE) {
                    text.push(ESCAPE);
                }
                text.push_str(command);
                text.push('\n');
            }
        }
    }
    text
}

/// A server index with each listed account index and its commands.
type ServerCommands = (usize, Vec<(usize, Vec<String>)>);

fn apply_logon_commands(
    document: &mut LauncherProfileDocument,
    text: &str,
) -> Result<(), ProfileError> {
    let mut errors = Vec::new();
    // (server, [(account, commands)]) in the order they appear.
    let mut changes: Vec<ServerCommands> = Vec::new();
    let mut server: Option<usize> = None;
    let mut server_seen = false;
    // True under a # or ## line that was wrong: its lines are skipped, the
    // error already reported.
    let mut skipping = false;
    // Where commands go: (change index, account entry index).
    let mut commands: Option<(usize, usize)> = None;
    for (number, line) in lines(text) {
        if let Some(command) = line.strip_prefix(ESCAPE) {
            if let Some((change, entry)) = commands {
                changes[change].1[entry].1.push(command.to_owned());
            } else if !skipping {
                errors.push(format!(
                    "Line {number}: a command needs an ##Account line above it."
                ));
            }
            continue;
        }
        if let Some(header) = line.strip_prefix(ACCOUNT_PREFIX) {
            commands = None;
            skipping = true;
            if !server_seen {
                errors.push(format!(
                    "Line {number}: put a #Server line above ##Account."
                ));
                continue;
            }
            let Some(server_index) = server else {
                continue;
            };
            let Some(name) = decode_header(header, number, &mut errors) else {
                continue;
            };
            let server_profile = &document.servers[server_index];
            let Some(account_index) = saved_entry(&server_profile.accounts, &name) else {
                errors.push(format!(
                    "Line {number}: server '{}' has no account '{name}'. Add it in Edit \
                     accounts first.",
                    server_profile.name
                ));
                continue;
            };
            let change = changes
                .iter()
                .position(|(index, _)| *index == server_index)
                .unwrap_or_default();
            let accounts = &mut changes[change].1;
            if accounts.iter().any(|(index, _)| *index == account_index) {
                errors.push(format!(
                    "Line {number}: account '{name}' appears more than once under '{}'.",
                    server_profile.name
                ));
                continue;
            }
            accounts.push((account_index, Vec::new()));
            commands = Some((change, accounts.len() - 1));
            skipping = false;
            continue;
        }
        if let Some(header) = line.strip_prefix(SERVER_PREFIX) {
            server_seen = true;
            commands = None;
            server = resolve_server(document, header, number, &mut errors);
            if let Some(index) = server {
                if changes.iter().any(|(existing, _)| *existing == index) {
                    errors.push(format!(
                        "Line {number}: server '{}' appears more than once.",
                        document.servers[index].name
                    ));
                    server = None;
                } else {
                    changes.push((index, Vec::new()));
                }
            }
            skipping = server.is_none();
            continue;
        }
        if let Some((change, entry)) = commands {
            changes[change].1[entry].1.push(line.to_owned());
        } else if skipping {
            // Belongs to a header line that was wrong and is already
            // reported.
        } else {
            errors.push(format!(
                "Line {number}: a command needs an ##Account line above it."
            ));
        }
    }
    throw_if_any(&errors)?;
    for (server_index, accounts) in changes {
        for (account_index, account) in document.servers[server_index]
            .accounts
            .iter_mut()
            .enumerate()
        {
            account.login_commands = accounts
                .iter()
                .find(|(index, _)| *index == account_index)
                .map(|(_, list)| list.clone())
                .unwrap_or_default();
        }
    }
    Ok(())
}

// --- Servers ------------------------------------------------------------------

fn apply_servers(document: &mut LauncherProfileDocument, text: &str) -> Result<(), ProfileError> {
    let mut servers = Vec::new();
    for (_, line) in lines(text) {
        let fields = split_fields(line, '|')?;
        require(
            fields.len() == 3,
            "Each server line must be name | host | port.",
        )?;
        let port = decode(fields[2])?.parse::<i32>().ok();
        require(
            port.is_some(),
            "Server ports must be numbers from 1 to 65535.",
        )?;
        servers.push((
            decode(fields[0])?,
            decode(fields[1])?,
            port.unwrap_or_default(),
        ));
    }
    require(
        servers.iter().all(|(name, host, port)| {
            !name.trim().is_empty() && !host.trim().is_empty() && (1..=65535).contains(port)
        }),
        "Each server needs a name, host and port from 1 to 65535.",
    )?;
    let distinct: HashSet<&str> = servers.iter().map(|(name, ..)| name.as_str()).collect();
    require(
        distinct.len() == servers.len(),
        "Server names must be unique.",
    )?;
    let known = copy_known_account_credentials(document);
    let old = std::mem::take(&mut document.servers);
    document.servers = servers
        .into_iter()
        .map(|(name, host, port)| {
            let accounts = old
                .iter()
                .find(|server| server.name == name)
                .map_or_else(|| known.clone(), |server| server.accounts.clone());
            ServerProfile {
                name,
                host,
                port,
                accounts,
                account_list_initialized: true,
            }
        })
        .collect();
    Ok(())
}

// --- Shared -------------------------------------------------------------------

/// A server named on a `#` line: its exact name, or failing that the one
/// server whose name matches ignoring case.
fn resolve_server(
    document: &LauncherProfileDocument,
    header: &str,
    number: usize,
    errors: &mut Vec<String>,
) -> Option<usize> {
    let name = decode_header(header, number, errors)?;
    if let Some(exact) = document
        .servers
        .iter()
        .position(|server| server.name == name)
    {
        return Some(exact);
    }
    let matches: Vec<usize> = document
        .servers
        .iter()
        .enumerate()
        .filter(|(_, server)| server.name.to_lowercase() == name.to_lowercase())
        .map(|(index, _)| index)
        .collect();
    if let [only] = matches.as_slice() {
        return Some(*only);
    }
    errors.push(if name.is_empty() {
        format!("Line {number}: a # line needs a server name.")
    } else {
        format!("Line {number}: there is no server named '{name}'. Add it with Add server first.")
    });
    None
}

/// A name after `#` or `##`, quoted like a value when it has edge spaces,
/// a quote, or starts with `#` itself.
fn encode_header(name: &str) -> String {
    if name != name.trim() || name.starts_with('#') || name.contains(['"', '\r', '\n', '\t']) {
        json::quote(name)
    } else {
        name.to_owned()
    }
}

fn decode_header(header: &str, number: usize, errors: &mut Vec<String>) -> Option<String> {
    match decode(header) {
        Ok(name) => Some(name),
        Err(error) => {
            errors.push(format!("Line {number}: {error}"));
            None
        }
    }
}

/// The text's non-empty lines, trimmed, with their 1-based line numbers.
fn lines(text: &str) -> impl Iterator<Item = (usize, &str)> {
    text.split('\n')
        .enumerate()
        .map(|(index, line)| (index + 1, line.trim()))
        .filter(|(_, line)| !line.is_empty())
}

fn throw_if_any(errors: &[String]) -> Result<(), ProfileError> {
    if errors.is_empty() {
        Ok(())
    } else {
        Err(ProfileError::Invalid(errors.join("\n")))
    }
}

fn encode(value: &str) -> String {
    if value != value.trim() || value.contains(['|', ',', '"', '\r', '\n', '\t']) {
        json::quote(value)
    } else {
        value.to_owned()
    }
}

fn decode(field: &str) -> Result<String, ProfileError> {
    let field = field.trim();
    if !field.contains('"') {
        return Ok(field.to_owned());
    }
    require(
        field.starts_with('"'),
        "A quoted value must have double quotes around all of it.",
    )?;
    host_json::parse(field.as_bytes())
        .ok()
        .and_then(|node| host_json::string_or_null(&node).ok())
        .map(Option::unwrap_or_default)
        .ok_or_else(|| {
            ProfileError::Invalid(
                "A quoted value is not valid. Put double quotes around a value that contains a \
                 comma or a quote; write a quote as \\\" and a backslash as \\\\."
                    .to_owned(),
            )
        })
}

fn split_fields(line: &str, separator: char) -> Result<Vec<&str>, ProfileError> {
    let mut fields = Vec::new();
    let mut quoted = false;
    let mut escaped = false;
    let mut start = 0;
    for (index, character) in line.char_indices() {
        if quoted && escaped {
            escaped = false;
            continue;
        }
        if quoted && character == '\\' {
            escaped = true;
            continue;
        }
        if character == '"' {
            quoted = !quoted;
            continue;
        }
        if !quoted && character == separator {
            fields.push(&line[start..index]);
            start = index + character.len_utf8();
        }
    }
    require(
        !quoted,
        "A quoted value is missing its closing double quote.",
    )?;
    fields.push(&line[start..]);
    Ok(fields)
}
