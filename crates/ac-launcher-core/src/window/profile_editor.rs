//! The profile editors: accounts and logon commands as one plain text each,
//! servers as name and address rows (OpenAC `ProfileTextEditorViewModel`).
//! The text is checked as a whole when saved; a wrong line is reported by
//! number and nothing is saved. A save goes through the orchestrator as one
//! validated, atomic profile change, and is refused when the profiles
//! changed while the editor was open.

use std::net::{Ipv4Addr, Ipv6Addr};

use super::Orchestration;
use crate::orchestration::OrchestratorError;
use crate::profiles::{
    LauncherProfileDocument, LauncherTextEditorKind, ProfileError, ServerProfile, apply_text,
    read_text,
};

/// What a hidden password shows as.
pub const HIDDEN_PASSWORD: &str = "••••••";

/// One server row of the servers editor: its name and `address:port`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProfileEntry {
    /// The server name.
    pub name: String,
    /// `address:port`.
    pub value: String,
}

impl ProfileEntry {
    /// The name field's label.
    pub fn name_label(&self) -> &'static str {
        "Server name"
    }

    /// The value field's label.
    pub fn value_label(&self) -> &'static str {
        "Server address and port"
    }

    /// The value field's hint.
    pub fn placeholder(&self) -> &'static str {
        "game.example.com:9000"
    }
}

/// The profile editor dialog.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProfileTextEditor {
    /// Whether it is shown.
    pub is_open: bool,
    /// The heading.
    pub title: String,
    /// The line under the heading.
    pub help_text: String,
    /// Why the last save failed.
    pub error: Option<String>,
    /// The server rows.
    pub rows: Vec<ProfileEntry>,
    /// Show passwords, in the accounts editor. Off by default, for a shared
    /// screen.
    pub show_passwords: bool,
    text: String,
    kind: LauncherTextEditorKind,
    original_text: String,
    removal_warning: Option<String>,
    confirmed_text: Option<String>,
}

impl Default for ProfileTextEditor {
    fn default() -> Self {
        Self {
            is_open: false,
            title: String::new(),
            help_text: String::new(),
            error: None,
            rows: Vec::new(),
            show_passwords: false,
            text: String::new(),
            kind: LauncherTextEditorKind::Accounts,
            original_text: String::new(),
            removal_warning: None,
            confirmed_text: None,
        }
    }
}

impl ProfileTextEditor {
    /// Which projection is open.
    pub fn kind(&self) -> LauncherTextEditorKind {
        self.kind
    }

    /// Servers are edited as rows.
    pub fn is_fields_editor(&self) -> bool {
        self.kind == LauncherTextEditorKind::Servers
    }

    /// Accounts and logon commands are edited as text.
    pub fn is_text_editor(&self) -> bool {
        !self.is_fields_editor()
    }

    /// The accounts text is open.
    pub fn is_accounts_editor(&self) -> bool {
        self.kind == LauncherTextEditorKind::Accounts
    }

    /// The logon commands text is open.
    pub fn is_logon_commands_editor(&self) -> bool {
        self.kind == LauncherTextEditorKind::LogonCommands
    }

    /// Passwords are hidden; the text box is then read-only, so a hidden
    /// password is never saved.
    pub fn is_password_hidden(&self) -> bool {
        self.is_accounts_editor() && !self.show_passwords
    }

    /// The text itself, passwords included.
    pub fn text(&self) -> &str {
        &self.text
    }

    /// Replaces the text. A confirmation was for the text as it was; a
    /// changed text is asked about again.
    pub fn set_text(&mut self, value: &str) {
        if self.text == value {
            return;
        }
        value.clone_into(&mut self.text);
        if self.confirmed_text.as_deref() != Some(self.text.as_str()) {
            self.confirmed_text = None;
            self.removal_warning = None;
        }
    }

    /// What the text box shows: the text, with passwords hidden unless
    /// Show passwords is on.
    pub fn displayed_text(&self) -> String {
        if self.is_password_hidden() {
            hide_passwords(&self.text)
        } else {
            self.text.clone()
        }
    }

    /// "2 servers, 8 accounts" under the logon commands, counted from the
    /// text as typed.
    pub fn summary(&self) -> String {
        if !self.is_logon_commands_editor() {
            return String::new();
        }
        let lines: Vec<&str> = self.text.split('\n').map(str::trim).collect();
        let servers = lines
            .iter()
            .filter(|line| line.starts_with('#') && !line.starts_with("##"))
            .count();
        let accounts = lines.iter().filter(|line| line.starts_with("##")).count();
        format!(
            "{servers} {}, {accounts} {} · an account with no lines runs no commands",
            if servers == 1 { "server" } else { "servers" },
            if accounts == 1 { "account" } else { "accounts" }
        )
    }

    /// The accounts the last Save would have removed with what is saved on
    /// them, asking to press Save again; `None` when there is nothing to
    /// confirm.
    pub fn removal_warning(&self) -> Option<&str> {
        self.removal_warning.as_deref()
    }

    /// The Save button's text.
    pub fn save_text(&self) -> &'static str {
        if self.removal_warning.is_some() {
            "Remove and save"
        } else {
            "Save"
        }
    }

    /// The first column's heading.
    pub fn first_column_label(&self) -> &'static str {
        "Server name"
    }

    /// The second column's heading.
    pub fn second_column_label(&self) -> &'static str {
        "Address:port"
    }

    /// The add button's text.
    pub fn add_row_text(&self) -> &'static str {
        "Add server"
    }

    /// Opens `kind` with what the profiles hold now.
    pub fn open(
        &mut self,
        orchestrator: &dyn Orchestration,
        kind: LauncherTextEditorKind,
    ) -> Result<(), OrchestratorError> {
        let original = orchestrator.read_profile_text(kind)?;
        self.kind = kind;
        self.title = match kind {
            LauncherTextEditorKind::Accounts => "Accounts",
            LauncherTextEditorKind::Servers => "Edit servers",
            LauncherTextEditorKind::LogonCommands => "Logon commands",
        }
        .into();
        self.help_text = match kind {
            LauncherTextEditorKind::Accounts => {
                "One account per line, in plain text, under its #Server line. Profiles tag \
                 accounts so you can filter and play them together."
            }
            LauncherTextEditorKind::Servers => {
                "Enter a server name and address including its port, such as \
                 game.example.com:9000. Keep the name to retain saved characters."
            }
            LauncherTextEditorKind::LogonCommands => {
                "Every server and account you have, in one plain text. Copy it all, paste it all."
            }
        }
        .into();
        self.error = None;
        self.show_passwords = false;
        self.confirmed_text = None;
        self.removal_warning = None;
        self.text = if self.is_text_editor() {
            original.clone()
        } else {
            String::new()
        };
        self.rows.clear();
        if self.is_fields_editor() {
            let mut draft = LauncherProfileDocument::default();
            apply_text(&mut draft, kind, &original)?;
            for server in draft.servers {
                let endpoint = format_endpoint(&server.host, server.port);
                self.add_row_with(server.name, endpoint);
            }
            if self.rows.is_empty() {
                self.add_row();
            }
        }
        self.original_text = original;
        self.is_open = true;
        Ok(())
    }

    /// Closes without saving and forgets every draft value (passwords too).
    pub fn close(&mut self) {
        self.is_open = false;
        self.confirmed_text = None;
        self.removal_warning = None;
        self.text.clear();
        self.original_text.clear();
        self.show_passwords = false;
        for row in &mut self.rows {
            row.value.clear();
        }
        self.rows.clear();
        self.error = None;
    }

    /// Adds an empty row.
    pub fn add_row(&mut self) {
        self.add_row_with(String::new(), String::new());
    }

    fn add_row_with(&mut self, name: String, value: String) {
        self.rows.push(ProfileEntry { name, value });
    }

    /// Removes row `index`, clearing its value first.
    pub fn remove_row(&mut self, index: usize) {
        if index < self.rows.len() {
            self.rows[index].value.clear();
            self.rows.remove(index);
        }
    }

    /// Saves; closes on success, keeps the editor open with the reason
    /// otherwise. A text that would remove accounts with something saved on
    /// them is saved only on a second press, after the warning names them.
    pub fn save(&mut self, orchestrator: &dyn Orchestration) {
        if !self.is_open {
            return;
        }
        if self.is_text_editor() && self.confirmed_text.as_deref() != Some(self.text.as_str()) {
            let removals = orchestrator
                .describe_profile_text_removals(self.kind, &self.text)
                .unwrap_or_default();
            if !removals.is_empty() {
                self.confirmed_text = Some(self.text.clone());
                self.error = None;
                self.removal_warning = Some(format!(
                    "Saving removes these accounts and everything saved on them: {}. A changed \
                     name is a different account, with its own characters. Press Remove and \
                     save to go ahead.",
                    removals.join("; ")
                ));
                return;
            }
        }
        let text = if self.is_fields_editor() {
            self.read_fields()
        } else {
            Ok(self.text.clone())
        };
        let result = text
            .map_err(OrchestratorError::from)
            .and_then(|text| orchestrator.save_profile_text(self.kind, &text, &self.original_text));
        match result {
            Ok(()) => self.close(),
            Err(OrchestratorError::Profile(
                error @ (ProfileError::Invalid(_) | ProfileError::BlankArgument(_)),
            )) => self.error = Some(error.to_string()),
            Err(OrchestratorError::Operation(message)) => self.error = Some(message),
            Err(_) => {
                self.error = Some(
                    "Unable to save. Stop active sessions and check that the profile folder is \
                     writable."
                        .into(),
                );
            }
        }
    }

    fn read_fields(&self) -> Result<String, ProfileError> {
        let draft = LauncherProfileDocument {
            servers: self
                .rows
                .iter()
                .map(|row| {
                    let (host, port) = parse_endpoint(&row.value)?;
                    Ok(ServerProfile::new(row.name.clone(), host, port))
                })
                .collect::<Result<_, ProfileError>>()?,
            ..LauncherProfileDocument::default()
        };
        Ok(read_text(&draft, self.kind))
    }
}

/// The accounts text with every password replaced by [`HIDDEN_PASSWORD`]:
/// a `Password=` field at the start of a line or after a comma, its value a
/// quoted string or everything up to the next comma or line end.
pub fn hide_passwords(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut lines = text.split('\n').peekable();
    while let Some(line) = lines.next() {
        out.push_str(&hide_line(line));
        if lines.peek().is_some() {
            out.push('\n');
        }
    }
    out
}

fn hide_line(line: &str) -> String {
    let bytes = line.as_bytes();
    let mut out = String::with_capacity(line.len());
    let mut copied = 0;
    let mut index = 0;
    while index <= bytes.len() {
        let at_field = index == 0 || bytes[index - 1] == b',';
        if at_field && let Some((key_end, value_end)) = password_field(line, index) {
            out.push_str(&line[copied..key_end]);
            out.push_str(HIDDEN_PASSWORD);
            copied = value_end;
            index = value_end.max(index + 1);
            continue;
        }
        index += 1;
    }
    out.push_str(&line[copied..]);
    out
}

/// At a field start, a `Password =` key: where the key ends (after `=`) and
/// where its value ends.
fn password_field(line: &str, start: usize) -> Option<(usize, usize)> {
    let bytes = line.as_bytes();
    let skip_blank = |mut at: usize| {
        while at < bytes.len() && matches!(bytes[at], b' ' | b'\t') {
            at += 1;
        }
        at
    };
    let key = skip_blank(start);
    let word = "password";
    if !line
        .get(key..key + word.len())
        .is_some_and(|candidate| candidate.eq_ignore_ascii_case(word))
    {
        return None;
    }
    let equals = skip_blank(key + word.len());
    if bytes.get(equals) != Some(&b'=') {
        return None;
    }
    let key_end = equals + 1;
    // A quoted value with its surrounding blanks, when the quote closes.
    let quote = skip_blank(key_end);
    if bytes.get(quote) == Some(&b'"') {
        let mut at = quote + 1;
        while at < bytes.len() {
            match bytes[at] {
                b'\\' if at + 1 < bytes.len() && bytes[at + 1] != b'\r' => at += 2,
                b'"' => return Some((key_end, skip_blank(at + 1))),
                b'\r' => break,
                _ => at += 1,
            }
        }
    }
    let value_end = line[key_end..]
        .find([',', '\r'])
        .map_or(line.len(), |offset| key_end + offset);
    Some((key_end, value_end))
}

/// `host:port`, with an IPv6 address in brackets.
pub fn format_endpoint(host: &str, port: i32) -> String {
    if host.contains(':') && !host.starts_with('[') {
        format!("[{host}]:{port}")
    } else {
        format!("{host}:{port}")
    }
}

/// Reads `host:port`: a host name or IPv4 address, or `[IPv6]`, and a port
/// from 1 to 65535.
pub fn parse_endpoint(value: &str) -> Result<(String, i32), ProfileError> {
    let address = value.trim();
    let port_error = || {
        ProfileError::Invalid(
            "Enter an address and port, for example game.example.com:9000. Ports must be from 1 \
             to 65535."
                .into(),
        )
    };
    let separator = address
        .rfind(':')
        .filter(|&index| index > 0)
        .ok_or_else(port_error)?;
    let port_text = &address[separator + 1..];
    if port_text.is_empty() || !port_text.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err(port_error());
    }
    let port = port_text
        .parse::<i32>()
        .ok()
        .filter(|port| (1..=65535).contains(port))
        .ok_or_else(port_error)?;
    let host = &address[..separator];
    if let Some(inner) = host
        .strip_prefix('[')
        .and_then(|rest| rest.strip_suffix(']'))
    {
        if inner.parse::<Ipv6Addr>().is_ok() {
            return Ok((inner.to_owned(), port));
        }
    } else if is_dns_name(host) || host.parse::<Ipv4Addr>().is_ok() {
        return Ok((host.to_owned(), port));
    }
    Err(ProfileError::Invalid(
        "Enter a hostname or IP address followed by its port. Use [IPv6 address]:port for IPv6."
            .into(),
    ))
}

/// A DNS host name as `Uri.CheckHostName` accepts one: dot-separated labels
/// of letters, digits, `-` and `_`, each 1 to 63 characters and starting
/// with a letter or digit, 255 characters at most, one trailing dot allowed.
fn is_dns_name(host: &str) -> bool {
    if host.is_empty() || host.len() > 255 {
        return false;
    }
    let trimmed = host.strip_suffix('.').unwrap_or(host);
    !trimmed.is_empty()
        && trimmed.split('.').all(|label| {
            !label.is_empty()
                && label.len() <= 63
                && label.starts_with(|c: char| c.is_ascii_alphanumeric())
                && label
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_')
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn endpoints_round_trip() {
        assert_eq!(
            parse_endpoint("example.com:9000").unwrap(),
            ("example.com".into(), 9000)
        );
        assert_eq!(
            parse_endpoint(" 127.0.0.1:1 ").unwrap(),
            ("127.0.0.1".into(), 1)
        );
        assert_eq!(
            parse_endpoint("[::1]:65535").unwrap(),
            ("::1".into(), 65535)
        );
        assert_eq!(format_endpoint("::1", 65535), "[::1]:65535");
        assert_eq!(format_endpoint("example.com", 9000), "example.com:9000");
    }

    // OpenAC ProfileTextEditorViewModel.HidePasswords (its password field
    // pattern): plain and quoted values, any case, leaving other fields.
    #[test]
    fn passwords_are_hidden_and_nothing_else() {
        let text = "#Coldeve\nName=a,Password=secret,Profiles=Bots\nName=b, password = \"x,\\\"y\" \
                    ,Profiles=Main\nName=c,Password=\n";
        assert_eq!(
            hide_passwords(text),
            "#Coldeve\nName=a,Password=••••••,Profiles=Bots\nName=b, password =••••••,Profiles=Main\n\
             Name=c,Password=••••••\n"
        );
        assert_eq!(hide_passwords("Name=Password"), "Name=Password");
    }
}
