//! Startup options: the one place command-line flags and environment
//! variables are read. Every option is documented in `docs/launch-options.md`.

use std::fmt;
use std::net::{SocketAddr, ToSocketAddrs};
use std::path::PathBuf;
use std::time::Duration;

/// Default server address (a local server).
pub const DEFAULT_SERVER: &str = "127.0.0.1:9000";

/// Usage text printed for `--help` and after an options error.
pub const USAGE: &str = "\
Usage: openac [--headless] [options]

Options:
  --headless              Run without any window support (no display needed).
                          Without it, the console commands /window and /close open
                          and close a game window on the running session.
  --server <host:port>    Login server address (env OPENAC_SERVER, default 127.0.0.1:9000).
  --account <name>        Account name (env OPENAC_ACCOUNT).
  --character <name>      Character to enter the world with (default: none; the session
                          waits at character selection for /enter or the window).
  --duration <seconds>    Log out after this many seconds in the world (default: until Ctrl+C).
  --log <level>           error | warn | info | debug | trace (env OPENAC_LOG, default info).
  --dat-dir <path>        Dat directory the window reads (env OPENAC_DAT_DIR, default
                          Documents\\Asheron's Call under the user profile).
  --landblock-radius <n>  Landblocks drawn around the player's, 0 to 10 (env
                          OPENAC_LANDBLOCK_RADIUS, default 2: a 5 x 5 square).
  --help                 Show this text.

The password comes only from the environment variable OPENAC_PASSWORD.
Chat logs (/log) and journals go under OPENAC_DATA_DIR (default: the platform
data directory). Typed lines that are not console commands are chat: /say,
/tell <name>, <text>, /r <text>, channels, /emote, /friends, ...
Simulated packet loss for testing: OPENAC_NET_DROP_PCT (1-100),
OPENAC_NET_DROP_SEED (integer), OPENAC_NET_DROP_DIR (in | out | both).";

/// Which direction simulated packet loss applies to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DropDirection {
    /// Datagrams from the server.
    In,
    /// Datagrams to the server.
    Out,
    /// Both directions.
    Both,
}

/// Simulated packet loss, for proving the transport recovers.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct NetLoss {
    /// Percentage of datagrams dropped, 1 to 100.
    pub percent: u8,
    /// Seed for the drop decisions, so a run can be repeated.
    pub seed: u32,
    /// Which direction loses datagrams.
    pub direction: DropDirection,
}

/// A password that never appears in logs or debug output.
#[derive(Clone, PartialEq, Eq)]
pub struct Password(String);

impl Password {
    /// The secret text.
    #[must_use]
    pub fn expose(&self) -> &str {
        &self.0
    }
}

impl fmt::Debug for Password {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("Password(***)")
    }
}

/// Log verbosity.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum LogLevel {
    /// Errors only.
    Error,
    /// Errors and warnings.
    Warn,
    /// Normal progress messages.
    Info,
    /// Detail for debugging.
    Debug,
    /// Everything.
    Trace,
}

/// Everything the program needs to start.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Options {
    /// Run without a window.
    pub headless: bool,
    /// Login server address.
    pub server: SocketAddr,
    /// Account name.
    pub account: String,
    /// Account password.
    pub password: Password,
    /// Character to play; `None` picks the first available one.
    pub character: Option<String>,
    /// Log out after this long in the world; `None` runs until stopped.
    pub duration: Option<Duration>,
    /// Simulated packet loss, off unless configured.
    pub net_loss: Option<NetLoss>,
    /// Log verbosity.
    pub log_level: LogLevel,
    /// Dat directory the window reads; `None` uses the usual install place.
    pub dat_dir: Option<PathBuf>,
    /// Landblocks drawn around the player's; `None` uses the default.
    pub landblock_radius: Option<u32>,
    /// Data directory for chat logs and journals (`OPENAC_DATA_DIR`);
    /// `None` uses the platform default.
    pub data_dir: Option<String>,
}

/// The data-directory variable, shared with `openac run`.
const DATA_DIR_VAR: &str = ac_host::paths::DATA_DIR_VAR;

/// What the command line asked for.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Command {
    /// Print usage and exit.
    Help,
    /// Run with these options.
    Run(Options),
}

/// A startup option was missing or malformed.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum OptionsError {
    /// An argument the program does not know.
    #[error("unknown argument `{0}`")]
    UnknownArgument(String),
    /// A flag that needs a value was last on the line.
    #[error("`{0}` needs a value")]
    MissingValue(&'static str),
    /// A value could not be understood.
    #[error("invalid value `{value}` for {name}: {reason}")]
    InvalidValue {
        /// Flag or environment variable name.
        name: &'static str,
        /// The value given.
        value: String,
        /// What was wrong with it.
        reason: &'static str,
    },
    /// A required option was not given.
    #[error("{0} is required")]
    Required(&'static str),
    /// A graphics setting could not be used (the message names it).
    #[error("{0}")]
    Graphics(String),
}

impl Command {
    /// Parse command-line arguments (without the program name) and the
    /// environment, given as a lookup function so tests control it.
    pub fn parse<I, E>(args: I, env: E) -> Result<Self, OptionsError>
    where
        I: IntoIterator<Item = String>,
        E: Fn(&str) -> Option<String>,
    {
        let mut headless = false;
        let mut server = env("OPENAC_SERVER");
        let mut account = env("OPENAC_ACCOUNT");
        let mut character = None;
        let mut duration = None;
        let mut log = env("OPENAC_LOG");
        let mut dat_dir = env("OPENAC_DAT_DIR").filter(|d| !d.is_empty());
        let mut radius = env("OPENAC_LANDBLOCK_RADIUS").filter(|r| !r.is_empty());

        let mut args = args.into_iter();
        while let Some(arg) = args.next() {
            let mut value =
                |name: &'static str| args.next().ok_or(OptionsError::MissingValue(name));
            match arg.as_str() {
                "--help" | "-h" => return Ok(Self::Help),
                "--headless" => headless = true,
                "--server" => server = Some(value("--server")?),
                "--account" => account = Some(value("--account")?),
                "--character" => character = Some(value("--character")?),
                "--duration" => duration = Some(parse_duration(&value("--duration")?)?),
                "--log" => log = Some(value("--log")?),
                "--dat-dir" => dat_dir = Some(value("--dat-dir")?),
                "--landblock-radius" => radius = Some(value("--landblock-radius")?),
                _ => return Err(OptionsError::UnknownArgument(arg)),
            }
        }

        let server = parse_server(server.as_deref().unwrap_or(DEFAULT_SERVER))?;
        let account = account
            .filter(|a| !a.is_empty())
            .ok_or(OptionsError::Required("--account or OPENAC_ACCOUNT"))?;
        let password = env("OPENAC_PASSWORD")
            .filter(|p| !p.is_empty())
            .map(Password)
            .ok_or(OptionsError::Required("OPENAC_PASSWORD"))?;
        let log_level = match log {
            Some(level) => parse_log_level(&level)?,
            None => LogLevel::Info,
        };
        let net_loss = parse_net_loss(&env)?;
        let landblock_radius = radius.as_deref().map(parse_radius).transpose()?;

        Ok(Self::Run(Options {
            headless,
            server,
            account,
            password,
            character,
            duration,
            net_loss,
            log_level,
            dat_dir: dat_dir.map(PathBuf::from),
            landblock_radius,
            data_dir: env(DATA_DIR_VAR).filter(|d| !d.trim().is_empty()),
        }))
    }
}

fn invalid(name: &'static str, value: &str, reason: &'static str) -> OptionsError {
    OptionsError::InvalidValue {
        name,
        value: value.to_owned(),
        reason,
    }
}

fn parse_server(text: &str) -> Result<SocketAddr, OptionsError> {
    let addr = text
        .to_socket_addrs()
        .map_err(|_| invalid("--server", text, "expected host:port"))?
        .next()
        .ok_or_else(|| invalid("--server", text, "host did not resolve"))?;
    // The server's second (connect) port is the login port plus one.
    if addr.port() == u16::MAX {
        return Err(invalid(
            "--server",
            text,
            "port must leave room for port + 1",
        ));
    }
    Ok(addr)
}

fn parse_duration(text: &str) -> Result<Duration, OptionsError> {
    text.parse::<u64>()
        .ok()
        .filter(|&s| s > 0)
        .map(Duration::from_secs)
        .ok_or_else(|| invalid("--duration", text, "expected a whole number of seconds"))
}

/// The largest landblock radius accepted (a 21 x 21 square).
pub const MAX_LANDBLOCK_RADIUS: u32 = 10;

/// A landblock radius, 0 to [`MAX_LANDBLOCK_RADIUS`].
pub fn parse_radius(text: &str) -> Result<u32, OptionsError> {
    text.parse::<u32>()
        .ok()
        .filter(|r| *r <= MAX_LANDBLOCK_RADIUS)
        .ok_or_else(|| invalid("--landblock-radius", text, "expected 0 to 10"))
}

fn parse_log_level(text: &str) -> Result<LogLevel, OptionsError> {
    match text.to_ascii_lowercase().as_str() {
        "error" => Ok(LogLevel::Error),
        "warn" => Ok(LogLevel::Warn),
        "info" => Ok(LogLevel::Info),
        "debug" => Ok(LogLevel::Debug),
        "trace" => Ok(LogLevel::Trace),
        _ => Err(invalid(
            "--log",
            text,
            "expected error, warn, info, debug or trace",
        )),
    }
}

fn parse_net_loss<E: Fn(&str) -> Option<String>>(env: &E) -> Result<Option<NetLoss>, OptionsError> {
    let Some(pct) = env("OPENAC_NET_DROP_PCT").filter(|p| !p.is_empty()) else {
        return Ok(None);
    };
    let percent = pct
        .parse::<u8>()
        .ok()
        .filter(|p| (1..=100).contains(p))
        .ok_or_else(|| invalid("OPENAC_NET_DROP_PCT", &pct, "expected 1 to 100"))?;
    let seed = match env("OPENAC_NET_DROP_SEED") {
        Some(s) => s
            .parse::<u32>()
            .map_err(|_| invalid("OPENAC_NET_DROP_SEED", &s, "expected an unsigned integer"))?,
        None => 0,
    };
    let direction = match env("OPENAC_NET_DROP_DIR")
        .as_deref()
        .map(str::to_ascii_lowercase)
    {
        None => DropDirection::Both,
        Some(d) => match d.as_str() {
            "in" => DropDirection::In,
            "out" => DropDirection::Out,
            "both" => DropDirection::Both,
            _ => {
                return Err(invalid(
                    "OPENAC_NET_DROP_DIR",
                    &d,
                    "expected in, out or both",
                ));
            }
        },
    };
    Ok(Some(NetLoss {
        percent,
        seed,
        direction,
    }))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    fn parse(args: &[&str], env: &[(&str, &str)]) -> Result<Command, OptionsError> {
        let env: HashMap<String, String> = env
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
            .collect();
        Command::parse(args.iter().map(|a| (*a).to_owned()), |k| {
            env.get(k).cloned()
        })
    }

    fn run(args: &[&str], env: &[(&str, &str)]) -> Options {
        match parse(args, env) {
            Ok(Command::Run(options)) => options,
            other => panic!("expected options, got {other:?}"),
        }
    }

    const CREDS: [(&str, &str); 2] = [("OPENAC_ACCOUNT", "acct"), ("OPENAC_PASSWORD", "pw")];

    #[test]
    fn minimal_headless_run_uses_defaults() {
        let o = run(&["--headless"], &CREDS);
        assert_eq!(o.server, "127.0.0.1:9000".parse().unwrap());
        assert_eq!(o.account, "acct");
        assert_eq!(o.password.expose(), "pw");
        assert_eq!(o.character, None);
        assert_eq!(o.duration, None);
        assert_eq!(o.net_loss, None);
        assert_eq!(o.log_level, LogLevel::Info);
    }

    #[test]
    fn flags_override_environment() {
        let o = run(
            &[
                "--headless",
                "--account",
                "other",
                "--character",
                "Bob",
                "--duration",
                "300",
            ],
            &CREDS,
        );
        assert_eq!(o.account, "other");
        assert_eq!(o.character.as_deref(), Some("Bob"));
        assert_eq!(o.duration, Some(Duration::from_secs(300)));
    }

    #[test]
    fn password_is_required_and_never_printed() {
        assert_eq!(
            parse(&["--headless"], &[("OPENAC_ACCOUNT", "a")]),
            Err(OptionsError::Required("OPENAC_PASSWORD"))
        );
        let o = run(&["--headless"], &CREDS);
        assert!(!format!("{o:?}").contains("pw\""));
    }

    #[test]
    fn window_support_is_the_default() {
        assert!(!run(&[], &CREDS).headless);
    }

    #[test]
    fn net_loss_reads_all_three_variables() {
        let mut env = CREDS.to_vec();
        env.extend([
            ("OPENAC_NET_DROP_PCT", "5"),
            ("OPENAC_NET_DROP_SEED", "42"),
            ("OPENAC_NET_DROP_DIR", "out"),
        ]);
        assert_eq!(
            run(&["--headless"], &env).net_loss,
            Some(NetLoss {
                percent: 5,
                seed: 42,
                direction: DropDirection::Out
            })
        );
        env[2] = ("OPENAC_NET_DROP_PCT", "0");
        assert!(parse(&["--headless"], &env).is_err());
    }

    #[test]
    fn bad_input_is_reported_not_ignored() {
        assert!(matches!(
            parse(&["--headless", "--bogus"], &CREDS),
            Err(OptionsError::UnknownArgument(_))
        ));
        assert_eq!(
            parse(&["--headless", "--duration"], &CREDS),
            Err(OptionsError::MissingValue("--duration"))
        );
        assert!(parse(&["--headless", "--duration", "-1"], &CREDS).is_err());
        assert!(parse(&["--headless", "--log", "loud"], &CREDS).is_err());
    }

    #[test]
    fn dat_dir_and_radius_flags_beat_the_environment() {
        let o = run(&[], &CREDS);
        assert_eq!((o.dat_dir, o.landblock_radius), (None, None));
        let mut env = CREDS.to_vec();
        env.extend([
            ("OPENAC_DAT_DIR", "envdir"),
            ("OPENAC_LANDBLOCK_RADIUS", "3"),
        ]);
        let o = run(&[], &env);
        assert_eq!(o.dat_dir, Some(PathBuf::from("envdir")));
        assert_eq!(o.landblock_radius, Some(3));
        let o = run(&["--dat-dir", "flagdir", "--landblock-radius", "0"], &env);
        assert_eq!(o.dat_dir, Some(PathBuf::from("flagdir")));
        assert_eq!(o.landblock_radius, Some(0));
        assert!(parse(&["--landblock-radius", "11"], &CREDS).is_err());
        assert!(parse(&["--landblock-radius", "x"], &CREDS).is_err());
    }

    #[test]
    fn help_wins() {
        assert_eq!(parse(&["--help"], &[]), Ok(Command::Help));
    }
}
