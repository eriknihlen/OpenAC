//! The host's command grammar.
//!
//! ```text
//! openac --help
//! openac validate --config <path> [path overrides]
//! openac run --config <path> [path overrides] [console options] [credentials]
//! ```
//!
//! Every option takes a value except `--console`; values cannot be blank; an
//! option may appear once; an unknown option is refused. Direct credentials
//! come as a pair and only for `run`.

use crate::credentials::{COMMAND_LINE_REFERENCE, DirectCredentials, Secret};
use crate::paths::PathOverrides;

/// Usage text for `--help`.
pub const HELP: &str = "\
openac

Usage:
  openac --help
  openac validate --config <path> [path overrides]
  openac run --config <path> [path overrides] [credentials]

Commands:
  validate   Validate a versioned session configuration without connecting.
  run        Run the configured sessions.

Path overrides:
  --root-dir <path>              The whole install folder.
  --config-dir <path>
  --data-dir <path>
  --cache-dir <path>

Console options (run mode):
  --console                      Read typed lines and print chat.
  --console-stream <stdout|stderr>
                                 Where the chat front end prints;
                                 stderr by default, so the JSON
                                 diagnostic lines keep stdout.

Direct single-session credentials:
  -user <account> -password <password>
  --user <account> --password <password>";

/// What the process was asked to do.
#[derive(Debug)]
pub enum Command {
    /// Print [`HELP`] and exit successfully.
    Help,
    /// Check the document and exit.
    Validate(CommandLine),
    /// Run the document's sessions.
    Run(CommandLine),
}

/// The options shared by `validate` and `run`.
#[derive(Debug)]
pub struct CommandLine {
    /// `--config`: the session-config document.
    pub config_path: String,
    /// `--root-dir`, `--config-dir`, `--data-dir`, `--cache-dir`.
    pub paths: PathOverrides,
    /// `--user` and `--password` (run only).
    pub direct_credentials: Option<DirectCredentials>,
    /// `--console` (run only).
    pub console: bool,
    /// `--console-stream` (run only).
    pub console_stream: Option<ConsoleStream>,
}

/// Where the console prints.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConsoleStream {
    /// Standard error, the default: diagnostic lines keep standard output.
    Stderr,
    /// Standard output, beside the diagnostic lines.
    Stdout,
}

impl ConsoleStream {
    /// `stdout` or `stderr`, ignoring ASCII case.
    pub fn parse(value: &str) -> Option<Self> {
        if value.eq_ignore_ascii_case("stdout") {
            Some(Self::Stdout)
        } else if value.eq_ignore_ascii_case("stderr") {
            Some(Self::Stderr)
        } else {
            None
        }
    }
}

/// A command line that does not fit the grammar. The message never repeats
/// an option's value.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
#[error("{0}")]
pub struct UsageError(pub &'static str);

/// Reads the command line (without the program name).
pub fn parse_command_line(arguments: &[String]) -> Result<Command, UsageError> {
    match arguments.first().map(String::as_str) {
        None | Some("--help" | "-h") => return Ok(Command::Help),
        _ => {}
    }
    if arguments.len() < 3 {
        return Err(UsageError("Missing command options."));
    }
    let is_run = match arguments[0].as_str() {
        "run" => true,
        "validate" => false,
        _ => return Err(UsageError("Unknown command.")),
    };

    let mut config_path = None;
    let mut root_directory = None;
    let mut config_directory = None;
    let mut data_directory = None;
    let mut cache_directory = None;
    let mut user = None;
    let mut password = None;
    let mut console = false;
    let mut console_stream = None;
    let mut index = 1;
    while index < arguments.len() {
        let name = arguments[index].as_str();
        if name == "--console" {
            console = true;
            index += 1;
            continue;
        }
        let Some(value) = arguments.get(index + 1) else {
            return Err(UsageError("Every command option requires a value."));
        };
        if value.trim().is_empty() {
            return Err(UsageError("Command option values cannot be empty."));
        }
        let value = value.clone();
        match name {
            "--config" => set_once(&mut config_path, value)?,
            "--root-dir" => set_once(&mut root_directory, value)?,
            "--config-dir" => set_once(&mut config_directory, value)?,
            "--data-dir" => set_once(&mut data_directory, value)?,
            "--cache-dir" => set_once(&mut cache_directory, value)?,
            "--console-stream" => {
                let Some(stream) = ConsoleStream::parse(&value) else {
                    return Err(UsageError("--console-stream must be 'stdout' or 'stderr'."));
                };
                set_once(&mut console_stream, stream)?;
            }
            "-user" | "--user" => set_once(&mut user, value)?,
            "-password" | "--password" => set_once(&mut password, value)?,
            _ => return Err(UsageError("Unknown command option.")),
        }
        index += 2;
    }

    let Some(config_path) = config_path else {
        return Err(UsageError("The --config option is required."));
    };
    let direct_credentials = match (user, password) {
        (None, None) => None,
        (Some(user), Some(password)) => Some(DirectCredentials {
            user,
            password: Secret::new(COMMAND_LINE_REFERENCE, password)
                .map_err(|_| UsageError("Command option values cannot be empty."))?,
        }),
        _ => {
            return Err(UsageError(
                "The user and password options must be supplied together.",
            ));
        }
    };
    if direct_credentials.is_some() && !is_run {
        return Err(UsageError(
            "Direct credentials are valid only for run mode.",
        ));
    }
    if console && !is_run {
        return Err(UsageError("--console is valid only for run mode."));
    }
    if console_stream.is_some() && !is_run {
        return Err(UsageError("--console-stream is valid only for run mode."));
    }

    let command_line = CommandLine {
        config_path,
        paths: PathOverrides {
            root_directory,
            config_directory,
            data_directory,
            cache_directory,
        },
        direct_credentials,
        console,
        console_stream,
    };
    Ok(if is_run {
        Command::Run(command_line)
    } else {
        Command::Validate(command_line)
    })
}

fn set_once<T>(slot: &mut Option<T>, value: T) -> Result<(), UsageError> {
    if slot.is_some() {
        return Err(UsageError("Command options cannot be repeated."));
    }
    *slot = Some(value);
    Ok(())
}

/// Environment variable turning the console on (`0` turns it off).
pub const CONSOLE_VAR: &str = "OPENAC_HEADLESS_CONSOLE";
/// Environment variable choosing the console's stream.
pub const CONSOLE_STREAM_VAR: &str = "OPENAC_HEADLESS_CONSOLE_STREAM";

/// Whether the console runs: on with `--console`; otherwise as the
/// environment variable says (anything but `0` is on); otherwise on exactly
/// when standard input is a terminal.
pub fn resolve_console(flag: bool, env: Option<&str>, standard_input_is_terminal: bool) -> bool {
    if flag {
        return true;
    }
    match env {
        None => standard_input_is_terminal,
        Some(value) => value != "0",
    }
}

/// Where the console prints: `--console-stream`, else the environment
/// variable, else standard error. A bad environment value is an error (the
/// command line's value was already checked).
pub fn resolve_console_stream(
    command_line: Option<ConsoleStream>,
    env: Option<&str>,
) -> Result<ConsoleStream, crate::config::ConfigError> {
    if let Some(stream) = command_line {
        return Ok(stream);
    }
    match env {
        None => Ok(ConsoleStream::Stderr),
        Some(value) => ConsoleStream::parse(value).ok_or_else(|| {
            crate::config::ConfigError::Invalid(format!(
                "{CONSOLE_STREAM_VAR} must be 'stdout' or 'stderr'."
            ))
        }),
    }
}
