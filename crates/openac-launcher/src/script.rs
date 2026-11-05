//! The launcher probe script (`OPENAC_LAUNCHER_SCRIPT`): a diagnostic that
//! drives the window's commands from a file, one line at a time, for
//! hands-free live checks and screenshots. Off unless the variable names a
//! file. A value written `env:NAME` is read from that environment variable
//! (passwords never sit in the script).
//!
//! Commands: `wait <seconds>`, `screenshot <file.png>`, `tab accounts|plugins`,
//! `open settings|log|first-run|update|edit-accounts|edit-servers|logon-commands|add-server`,
//! `open row-options <account> <server>`, `open install <plugin id>`,
//! `open remove <plugin id>`, `close`, `first-run-dir <path>`, `first-run-save`,
//! `editor-row <index> <name> <value>`, `editor-add-row`, `editor-line <text>` (a line
//! appended to a text editor),
//! `editor-show-passwords on|off`, `editor-save`,
//! `check <account> <server> on|off`, `character <account> <server> <name>`,
//! `mode <account> <server> graphical|headless`, `play <account> <server>`,
//! `play-selected`, `stop <account> <server>`, `console <account> <server>`, `close-consoles`,
//! `refresh-plugins`, `wait-plugins <seconds>`, `install-choice none|all`,
//! `install-confirm`, `remove-confirm`, `console-send <account> <server> <text>`,
//! `check-servers`,
//! `wait-in-world <account> <server> <seconds>`, `wait-ended <account> <server> <seconds>`,
//! `wait-window <account> <server> graphical|headless <seconds>` (a running row's
//! window as its client reports it), `close-launcher` (the close button: asks
//! first while clients run), `close-cancel`,
//! `print <text>`, `quit`. Arguments with spaces go in double quotes.

use std::path::PathBuf;
use std::time::{Duration, Instant};

use ac_launcher_core::profiles::{LauncherTextEditorKind, same_account};
use ac_launcher_core::window::accounts::RowLaunchMode;
use ac_launcher_core::window::install_dialog::PluginEnableChoice;
use ac_launcher_core::window::{LauncherMainTab, LauncherWindow};

/// The variable naming the script file.
pub const SCRIPT_VARIABLE: &str = "OPENAC_LAUNCHER_SCRIPT";

/// What the window should do after a tick.
#[derive(Debug, Clone, PartialEq)]
pub enum ScriptAction {
    /// Capture the next frame to this file.
    Screenshot(PathBuf),
    /// Resize the window to this inner size, in logical pixels (the
    /// window's minimum still applies).
    WindowSize(f32, f32),
    /// Rebuild the window's GPU state, as after a lost device.
    RebuildGpu,
    /// Close the window.
    Quit,
}

enum Waiting {
    Until(Instant),
    InWorld {
        account: String,
        server: String,
        deadline: Instant,
    },
    Ended {
        account: String,
        server: String,
        deadline: Instant,
    },
    /// A screenshot once a just-opened dialog has finished fading in.
    Settle(Instant, PathBuf),
    /// Until no plugin check, Discover check or install is running.
    PluginsIdle(Instant),
    /// Until a running row reports a window open (true) or closed.
    Window {
        account: String,
        server: String,
        open: bool,
        deadline: Instant,
    },
}

/// How long a dialog takes to fade in fully.
const SETTLE: Duration = Duration::from_millis(700);

/// A loaded script and where it is.
pub struct Script {
    lines: Vec<(usize, Vec<String>)>,
    next: usize,
    waiting: Option<Waiting>,
}

impl std::fmt::Debug for Script {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Script")
            .field("next", &self.next)
            .field("lines", &self.lines.len())
            .finish_non_exhaustive()
    }
}

/// Splits a line into words; `"..."` keeps spaces.
fn tokenize(line: &str) -> Vec<String> {
    let mut words = Vec::new();
    let mut current = String::new();
    let mut quoted = false;
    let mut any = false;
    for character in line.chars() {
        match character {
            '"' => {
                quoted = !quoted;
                any = true;
            }
            c if c.is_whitespace() && !quoted => {
                if any {
                    words.push(std::mem::take(&mut current));
                    any = false;
                }
            }
            c => {
                current.push(c);
                any = true;
            }
        }
    }
    if any {
        words.push(current);
    }
    words
}

fn value(word: &str) -> String {
    match word.strip_prefix("env:") {
        Some(name) => std::env::var(name).unwrap_or_default(),
        None => word.to_owned(),
    }
}

impl Script {
    /// Reads the script named by [`SCRIPT_VARIABLE`], if any.
    pub fn from_environment() -> Option<Result<Self, String>> {
        let path = std::env::var(SCRIPT_VARIABLE).ok()?;
        if path.trim().is_empty() {
            return None;
        }
        Some(Self::load(&path))
    }

    fn load(path: &str) -> Result<Self, String> {
        let text = std::fs::read_to_string(path)
            .map_err(|error| format!("cannot read the launcher script {path}: {error}"))?;
        let lines = text
            .lines()
            .enumerate()
            .map(|(index, line)| (index + 1, tokenize(line)))
            .filter(|(_, words)| !words.is_empty() && !words[0].starts_with('#'))
            .collect();
        Ok(Self {
            lines,
            next: 0,
            waiting: None,
        })
    }

    /// Whether every line ran.
    pub fn is_finished(&self) -> bool {
        self.next >= self.lines.len() && self.waiting.is_none()
    }

    /// Runs lines until one waits; returns what the window must do.
    pub fn tick(&mut self, window: &mut LauncherWindow, now: Instant) -> Option<ScriptAction> {
        if let Some(waiting) = self.waiting.take() {
            match waiting {
                Waiting::Until(at) if now < at => {
                    self.waiting = Some(Waiting::Until(at));
                    return None;
                }
                Waiting::InWorld {
                    account,
                    server,
                    deadline,
                } => {
                    let state = newest_state(window, &account, &server);
                    if state.as_deref() != Some("In game") {
                        if now < deadline && state.as_deref() != Some("Failed") {
                            self.waiting = Some(Waiting::InWorld {
                                account,
                                server,
                                deadline,
                            });
                            return None;
                        }
                        println!(
                            "script: {account} on {server} did not reach the world ({state:?})"
                        );
                    } else {
                        println!("script: {account} on {server} is in the world");
                    }
                }
                Waiting::Ended {
                    account,
                    server,
                    deadline,
                } => {
                    let active = window
                        .row(&account, &server)
                        .is_some_and(|row| row.is_active());
                    if active && now < deadline {
                        self.waiting = Some(Waiting::Ended {
                            account,
                            server,
                            deadline,
                        });
                        return None;
                    }
                    println!(
                        "script: {account} on {server} {}",
                        if active { "is still running" } else { "ended" }
                    );
                }
                Waiting::Settle(at, path) => {
                    if now < at {
                        self.waiting = Some(Waiting::Settle(at, path));
                        return None;
                    }
                    return Some(ScriptAction::Screenshot(path));
                }
                Waiting::PluginsIdle(deadline) => {
                    let panel = &window.plugins;
                    let busy = panel.is_busy
                        || panel.is_discover_checking
                        || panel.discover_pending_count > 0
                        || panel.install_dialog.is_busy;
                    if busy && now < deadline {
                        self.waiting = Some(Waiting::PluginsIdle(deadline));
                        return None;
                    }
                    println!(
                        "script: plugins {}{}",
                        if busy { "still busy" } else { "idle" },
                        panel
                            .error
                            .as_deref()
                            .or(panel.install_dialog.error.as_deref())
                            .map(|error| format!(" (error: {error})"))
                            .unwrap_or_default()
                    );
                }
                Waiting::Window {
                    account,
                    server,
                    open,
                    deadline,
                } => {
                    let row = window.row(&account, &server);
                    let reached = row.is_some_and(|row| {
                        row.can_switch_window()
                            && (row.displayed_launch_mode() == RowLaunchMode::Graphical) == open
                    });
                    if !reached && now < deadline {
                        self.waiting = Some(Waiting::Window {
                            account,
                            server,
                            open,
                            deadline,
                        });
                        return None;
                    }
                    println!(
                        "script: {account} on {server} {} (status line: {:?})",
                        match (reached, open) {
                            (true, true) => "has its window open",
                            (true, false) => "runs without a window",
                            (false, _) => "did not switch",
                        },
                        window.last_error
                    );
                }
                Waiting::Until(_) => {}
            }
        }
        while self.next < self.lines.len() {
            let (number, words) = self.lines[self.next].clone();
            self.next += 1;
            println!("script: {}", redact(&words));
            match self.run(window, &words, now) {
                Ok(Some(action)) => return Some(action),
                Ok(None) => {}
                Err(error) => println!("script: line {number}: {error}"),
            }
            if self.waiting.is_some() {
                return None;
            }
        }
        None
    }

    fn run(
        &mut self,
        window: &mut LauncherWindow,
        words: &[String],
        now: Instant,
    ) -> Result<Option<ScriptAction>, String> {
        let arg = |index: usize| -> Result<String, String> {
            words
                .get(index)
                .map(|word| value(word))
                .ok_or_else(|| format!("'{}' needs more arguments", words[0]))
        };
        let seconds = |index: usize| -> Result<Duration, String> {
            arg(index)?
                .parse::<f64>()
                .map(Duration::from_secs_f64)
                .map_err(|_| "expected a number of seconds".to_owned())
        };
        match words[0].as_str() {
            "wait" => self.waiting = Some(Waiting::Until(now + seconds(1)?)),
            "screenshot" => {
                // A moment later, so a dialog opened just before has faded in.
                self.waiting = Some(Waiting::Settle(now + SETTLE, PathBuf::from(arg(1)?)));
            }
            "window-size" => {
                let pixels = |index: usize| -> Result<f32, String> {
                    arg(index)?
                        .parse::<f32>()
                        .ok()
                        .filter(|value| value.is_finite() && *value > 0.0)
                        .ok_or_else(|| "expected a width and height in pixels".to_owned())
                };
                return Ok(Some(ScriptAction::WindowSize(pixels(1)?, pixels(2)?)));
            }
            "close-consoles" => {
                for console in &mut window.consoles {
                    console.is_open = false;
                }
            }
            "rebuild-gpu" => return Ok(Some(ScriptAction::RebuildGpu)),
            "print" => println!("script: {}", words[1..].join(" ")),
            "tab" => match arg(1)?.as_str() {
                "accounts" => window.selected_tab = LauncherMainTab::Accounts,
                "plugins" => window.select_plugins_tab(),
                other => return Err(format!("unknown tab '{other}'")),
            },
            "open" => match arg(1)?.as_str() {
                "settings" => window.open_settings(),
                "log" => window.open_session_log(),
                "first-run" => window.open_first_run(),
                "update" => window.update_prompt.is_open = true,
                "edit-accounts" => window.open_text_editor(LauncherTextEditorKind::Accounts),
                "edit-servers" => window.open_text_editor(LauncherTextEditorKind::Servers),
                "logon-commands" => window.open_text_editor(LauncherTextEditorKind::LogonCommands),
                "add-server" => window.open_add_server(),
                "profiles" => window.open_profiles(),
                "row-options" => window.open_account_plugins(&arg(2)?, &arg(3)?),
                "character-plugins" => window.open_character_plugins(&arg(2)?, &arg(3)?),
                "install" => window.open_discover_install(&arg(2)?),
                "remove" => window.open_remove_dialog(&arg(2)?),
                other => return Err(format!("unknown dialog '{other}'")),
            },
            "close" => window.close_active_modal(),
            "first-run-dir" => window.set_first_run_directory(&arg(1)?),
            "first-run-save" => {
                window.start_first_run();
                window.first_run.acknowledge_completion();
            }
            "editor-row" => {
                let index: usize = arg(1)?.parse().map_err(|_| "expected a row number")?;
                while window.text_editor.rows.len() <= index {
                    window.text_editor.add_row();
                }
                window.text_editor.rows[index].name = arg(2)?;
                window.text_editor.rows[index].value = arg(3)?;
            }
            "editor-add-row" => window.text_editor.add_row(),
            "editor-line" => {
                // One line appended to a text editor (accounts, logon commands).
                let mut text = window.text_editor.text().to_owned();
                if !text.is_empty() && !text.ends_with('\n') {
                    text.push('\n');
                }
                let line: Vec<String> = words[1..].iter().map(|word| value(word)).collect();
                text.push_str(&line.join(" "));
                window.text_editor.set_text(&text);
            }
            "editor-show-passwords" => window.text_editor.show_passwords = arg(1)? == "on",
            "editor-save" => {
                window.save_text_editor();
                if let Some(error) = &window.text_editor.error {
                    return Err(format!("the editor refused: {error}"));
                }
            }
            "check" => window.set_row_checked(&arg(1)?, &arg(2)?, arg(3)? == "on"),
            "character" => window.set_row_character(&arg(1)?, &arg(2)?, &arg(3)?),
            "mode" => {
                let mode = match arg(3)?.as_str() {
                    "headless" => RowLaunchMode::Headless,
                    _ => RowLaunchMode::Graphical,
                };
                window.set_row_launch_mode(&arg(1)?, &arg(2)?, mode);
            }
            "play" => window.launch_row(&arg(1)?, &arg(2)?),
            "play-selected" => window.launch_checked(),
            "stop" => window.stop_row(&arg(1)?, &arg(2)?),
            "console" => window.open_console(&arg(1)?, &arg(2)?),
            "refresh-plugins" => window.plugins_check_now(),
            "wait-plugins" => {
                self.waiting = Some(Waiting::PluginsIdle(now + seconds(1)?));
            }
            "install-choice" => {
                window.plugins.install_dialog.choice = match arg(1)?.as_str() {
                    "none" => PluginEnableChoice::None,
                    "all" => PluginEnableChoice::All,
                    other => return Err(format!("unknown enable choice '{other}'")),
                };
            }
            "install-confirm" => {
                if !window.plugins.install_dialog.can_confirm() {
                    return Err("the install dialog cannot confirm now".into());
                }
                window.confirm_install();
            }
            "remove-confirm" => window.confirm_remove(),
            "console-send" => {
                let (account, server) = (arg(1)?, arg(2)?);
                window.open_console(&account, &server);
                let id = window
                    .row(&account, &server)
                    .and_then(|row| row.active_session_id())
                    .ok_or("that row has no running session")?;
                let index = window
                    .consoles
                    .iter()
                    .position(|console| console.session_id == id)
                    .ok_or("that session has no console")?;
                window.consoles[index].input = words[3..]
                    .iter()
                    .map(|word| value(word))
                    .collect::<Vec<_>>()
                    .join(" ");
                window.send_console_line(index);
            }
            "check-servers" => window.check_servers(),
            "wait-in-world" => {
                self.waiting = Some(Waiting::InWorld {
                    account: arg(1)?,
                    server: arg(2)?,
                    deadline: now + seconds(3)?,
                });
            }
            "wait-ended" => {
                self.waiting = Some(Waiting::Ended {
                    account: arg(1)?,
                    server: arg(2)?,
                    deadline: now + seconds(3)?,
                });
            }
            "wait-window" => {
                let open = match arg(3)?.as_str() {
                    "graphical" => true,
                    "headless" => false,
                    _ => return Err("expected graphical or headless".into()),
                };
                self.waiting = Some(Waiting::Window {
                    account: arg(1)?,
                    server: arg(2)?,
                    open,
                    deadline: now + seconds(4)?,
                });
            }
            "close-launcher" => {
                if window.request_close() {
                    println!("script: the launcher may close at once");
                } else if let Some(prompt) = window.close_prompt {
                    println!("script: close prompt: {}", prompt.message());
                }
            }
            "close-cancel" => window.cancel_close(),
            "quit" => return Ok(Some(ScriptAction::Quit)),
            other => return Err(format!("unknown command '{other}'")),
        }
        Ok(None)
    }
}

/// The newest session's state for a row, in the session log's words.
fn newest_state(window: &LauncherWindow, account: &str, server: &str) -> Option<String> {
    window
        .sessions
        .iter()
        .find(|session| same_account(&session.account, account) && session.server == server)
        .map(|session| session.state.clone())
}

/// The line as printed: `env:` values stay names.
fn redact(words: &[String]) -> String {
    words
        .iter()
        .map(|word| {
            if word.contains(' ') {
                format!("\"{word}\"")
            } else {
                word.clone()
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn words_split_on_spaces_and_keep_quoted_ones() {
        assert_eq!(
            tokenize(r#"play testaccount2 "Local ACE""#),
            vec!["play", "testaccount2", "Local ACE"]
        );
        assert_eq!(tokenize("  wait   2 "), vec!["wait", "2"]);
        assert_eq!(
            tokenize(r#"editor-row 0 "" """#),
            vec!["editor-row", "0", "", ""]
        );
    }
}
