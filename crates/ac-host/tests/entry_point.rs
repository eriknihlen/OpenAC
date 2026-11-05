//! Command grammar, startup order and exit codes.
//!
//! Ported from OpenAC `tests/AcDream.Headless.Tests/HeadlessEntryPointTests.cs` and
//! `LauncherHeadlessCommandLineContractTests.cs` (the launcher's argument lists are
//! taken from `src/AcDream.Launcher.Core/Orchestration/LauncherExecutableSet.cs`).

mod common;

use ac_host::{
    Command, ConsoleStream, ExitCode, HELP, Os, Startup, UsageError, parse_command_line, start,
};
use common::{FixturePlatform, TempFile, configuration_with, session};

fn args(list: &[&str]) -> Vec<String> {
    list.iter().map(|&arg| arg.to_owned()).collect()
}

/// Runs startup like a host's `main` and returns (exit code, stdout, stderr).
fn run(list: &[&str]) -> (u8, String, String) {
    run_on(list, &FixturePlatform::new(Os::current()))
}

fn run_on(list: &[&str], platform: &FixturePlatform) -> (u8, String, String) {
    match start(&args(list), platform, false) {
        Ok(Startup::Help) => (ExitCode::Success.code(), HELP.to_owned(), String::new()),
        Ok(Startup::Validated { summary, .. }) => {
            (ExitCode::Success.code(), summary, String::new())
        }
        Ok(Startup::Run(_)) => (ExitCode::Success.code(), "run".to_owned(), String::new()),
        Err(error) => (error.exit_code().code(), String::new(), error.report_line()),
    }
}

#[test]
fn help_is_presentation_free_and_successful() {
    for argument in ["--help", "-h"] {
        let (code, output, error) = run(&[argument]);
        assert_eq!(code, 0);
        assert!(output.contains("openac"));
        assert!(output.contains("--password"));
        assert_eq!(error, "");
    }
    assert!(matches!(parse_command_line(&[]), Ok(Command::Help)));
}

#[test]
fn run_command_accepts_direct_credential_aliases() {
    let Ok(Command::Run(command)) = parse_command_line(&args(&[
        "run",
        "--config",
        "bot.json",
        "-user",
        "testaccount",
        "--password",
        "fixture-password",
    ])) else {
        panic!("run command expected");
    };
    let direct = command.direct_credentials.unwrap();
    assert_eq!(direct.user, "testaccount");
    assert_eq!(direct.password.reveal(), "fixture-password");
    assert!(!format!("{direct:?}").contains("fixture-password"));
}

#[test]
fn direct_credential_pair_must_be_complete() {
    for (option, value) in [("-user", "testaccount"), ("--password", "fixture-password")] {
        let result = parse_command_line(&args(&["run", "--config", "bot.json", option, value]));
        assert!(matches!(result, Err(UsageError(_))));
    }
}

#[test]
fn direct_credentials_and_console_are_run_only() {
    for extra in [
        &["--user", "a", "--password", "b"][..],
        &["--console"][..],
        &["--console-stream", "stdout"][..],
    ] {
        let mut list = vec!["validate", "--config", "bot.json"];
        list.extend_from_slice(extra);
        assert!(parse_command_line(&args(&list)).is_err(), "{extra:?}");
    }
}

#[test]
fn grammar_refuses_repeats_blanks_unknowns_and_missing_values() {
    for list in [
        &["run", "--config", "a.json", "--config", "b.json"][..],
        &["run", "--config", " "][..],
        &["run", "--config", "a.json", "--bogus", "x"][..],
        &["run", "--config", "a.json", "--data-dir"][..],
        &["run", "--data-dir", "d"][..],
        &["run", "--config"][..],
        &["run", "--config", "a.json", "--console-stream", "file"][..],
    ] {
        assert!(parse_command_line(&args(list)).is_err(), "{list:?}");
    }
}

#[test]
fn grammar_reads_path_overrides_and_console_options() {
    let Ok(Command::Run(command)) = parse_command_line(&args(&[
        "run",
        "--config",
        "c.json",
        "--console",
        "--config-dir",
        "cfg",
        "--data-dir",
        "data",
        "--cache-dir",
        "cache",
        "--console-stream",
        "STDOUT",
    ])) else {
        panic!("run command expected");
    };
    assert_eq!(command.config_path, "c.json");
    assert_eq!(command.paths.config_directory.as_deref(), Some("cfg"));
    assert_eq!(command.paths.data_directory.as_deref(), Some("data"));
    assert_eq!(command.paths.cache_directory.as_deref(), Some("cache"));
    assert!(command.console);
    assert_eq!(command.console_stream, Some(ConsoleStream::Stdout));
}

#[test]
fn validate_accepts_strict_empty_no_connect_configuration() {
    let file = TempFile::with("entry", r#"{"version":1,"sessions":[]}"#);
    let (code, output, error) = run(&["validate", "--config", &file.path_string()]);
    assert_eq!(code, 0);
    assert!(output.contains("0 session(s)"));
    assert_eq!(output, "Configuration valid: version 1, 0 session(s).");
    assert_eq!(error, "");
}

#[test]
fn validate_rejects_invalid_or_secret_shaped_configuration() {
    for (json, expected) in [
        (
            r#"{"version":2,"sessions":[]}"#,
            "Unsupported configuration version",
        ),
        (
            r#"{"version":1,"sessions":[],"password":"secret"}"#,
            "JSON document is invalid",
        ),
        (r#"{"version":1,"sessions":[null]}"#, "non-empty id"),
        (r#"{"version":1}"#, "JSON document is invalid"),
    ] {
        let file = TempFile::with("entry", json);
        let (code, output, error) = run(&["validate", "--config", &file.path_string()]);
        assert_eq!(code, 3, "{json}");
        assert!(
            error.to_lowercase().contains(&expected.to_lowercase()),
            "{error}"
        );
        assert!(!error.contains("secret"));
        assert_eq!(output, "");
    }
}

#[test]
fn validate_accepts_complete_single_session_configuration() {
    let file = TempFile::with(
        "entry",
        &configuration_with(&[session("bot", "BOT_PASSWORD", None)]),
    );
    let (code, output, error) = run(&["validate", "--config", &file.path_string()]);
    assert_eq!(code, 0);
    assert!(output.contains("1 session(s)"));
    assert_eq!(error, "");
}

#[test]
fn validate_accepts_complete_process_content_descriptor() {
    let file = TempFile::with(
        "entry",
        r#"{"version":1,"process":{"content":{"datDirectory":"C:\\AC","preparedAssetPath":"C:\\AC\\acdream.pak"}},"sessions":[]}"#,
    );
    let (code, output, error) = run(&["validate", "--config", &file.path_string()]);
    assert_eq!(code, 0);
    assert!(output.contains("0 session(s)"));
    assert_eq!(error, "");
}

#[test]
fn validate_rejects_incomplete_process_content_descriptor() {
    for json in [
        r#"{"version":1,"process":{"content":{"datDirectory":"","preparedAssetPath":"package.pak"}},"sessions":[]}"#,
        r#"{"version":1,"process":{"content":{"datDirectory":"dats","preparedAssetPath":""}},"sessions":[]}"#,
    ] {
        let file = TempFile::with("entry", json);
        let (code, output, error) = run(&["validate", "--config", &file.path_string()]);
        assert_eq!(code, 3);
        assert!(error.contains("process.content"), "{error}");
        assert_eq!(output, "");
    }
}

#[test]
fn validate_rejects_ambiguous_session_ownership() {
    for (second, expected) in [
        (
            session("bot", "SECOND_PASSWORD", None),
            "Duplicate session id",
        ),
        (session("second", "BOT_PASSWORD", None), "already in use"),
    ] {
        let file = TempFile::with(
            "entry",
            &configuration_with(&[session("bot", "BOT_PASSWORD", None), second]),
        );
        let (code, _, error) = run(&["validate", "--config", &file.path_string()]);
        assert_eq!(code, 3);
        assert!(error.contains(expected), "{error}");
    }
}

#[test]
fn play_session_missing_character_or_policy_keeps_configuration_error_exit_code() {
    for (json, expected) in [
        (
            r#"{"version":1,"sessions":[{"id":"s","endpoint":{"host":"127.0.0.1","port":9000},"account":"account","policy":{"id":"idle"},"credential":{"provider":"environment","reference":"X"}}]}"#,
            "character selector",
        ),
        (
            r#"{"version":1,"sessions":[{"id":"s","endpoint":{"host":"127.0.0.1","port":9000},"account":"account","character":{"index":0},"credential":{"provider":"environment","reference":"X"}}]}"#,
            "policy id",
        ),
    ] {
        let file = TempFile::with("entry", json);
        let (code, output, error) = run(&["validate", "--config", &file.path_string()]);
        assert_eq!(code, ExitCode::Configuration.code());
        assert!(error.to_lowercase().contains(expected), "{error}");
        assert_eq!(output, "");
    }
}

#[test]
fn validate_accepts_probe_session_omitting_character_and_policy() {
    let file = TempFile::with(
        "entry",
        r#"{"version":1,"sessions":[{"id":"probe","endpoint":{"host":"127.0.0.1","port":9000},"account":"account","mode":"probe","credential":{"provider":"environment","reference":"X"}}]}"#,
    );
    let (code, output, error) = run(&["validate", "--config", &file.path_string()]);
    assert_eq!(code, ExitCode::Success.code());
    assert!(output.contains("1 session(s)"));
    assert_eq!(error, "");
}

#[test]
fn unknown_command_returns_usage_error() {
    let (code, output, error) = run(&["connect", "--password", "secret"]);
    assert_eq!(code, 2);
    assert!(!error.contains("secret"));
    assert_eq!(error, "Invalid command. Run --help for usage.");
    assert_eq!(output, "");
}

#[test]
fn missing_configuration_file_is_a_configuration_error() {
    let file = TempFile::reserve("entry-missing", ".json");
    let (code, _, error) = run(&["validate", "--config", &file.path_string()]);
    assert_eq!(code, 3);
    assert!(error.starts_with("Configuration invalid: could not read"));
}

#[test]
fn run_requires_a_session_and_direct_credentials_require_exactly_one() {
    let empty = TempFile::with("entry", r#"{"version":1,"sessions":[]}"#);
    let (code, _, error) = run(&["run", "--config", &empty.path_string()]);
    assert_eq!(code, 3);
    assert!(error.contains("at least one session"));

    let two = TempFile::with(
        "entry",
        &configuration_with(&[session("a", "A", None), session("b", "B", None)]),
    );
    let (code, _, error) = run(&[
        "run",
        "--config",
        &two.path_string(),
        "--user",
        "u",
        "--password",
        "p",
    ]);
    assert_eq!(code, 3);
    assert!(error.contains("exactly one configured session"));

    let one = TempFile::with("entry", &configuration_with(&[session("a", "A", None)]));
    let (code, output, _) = run(&[
        "run",
        "--config",
        &one.path_string(),
        "--user",
        "u",
        "--password",
        "p",
    ]);
    assert_eq!((code, output.as_str()), (0, "run"));
}

#[test]
fn a_bad_console_stream_variable_is_a_configuration_error() {
    let one = TempFile::with("entry", &configuration_with(&[session("a", "A", None)]));
    let platform =
        FixturePlatform::new(Os::current()).with_var("OPENAC_HEADLESS_CONSOLE_STREAM", "file");
    let (code, _, error) = run_on(&["run", "--config", &one.path_string()], &platform);
    assert_eq!(code, 3);
    assert!(error.contains("OPENAC_HEADLESS_CONSOLE_STREAM"));
}

#[test]
fn console_resolution_follows_flag_then_variable_then_terminal() {
    use ac_host::command::{resolve_console, resolve_console_stream};
    assert!(resolve_console(true, Some("0"), false));
    assert!(!resolve_console(false, Some("0"), true));
    assert!(resolve_console(false, Some("yes"), false));
    assert!(resolve_console(false, None, true));
    assert!(!resolve_console(false, None, false));
    assert_eq!(
        resolve_console_stream(None, None).unwrap(),
        ConsoleStream::Stderr
    );
    assert_eq!(
        resolve_console_stream(None, Some("Stdout")).unwrap(),
        ConsoleStream::Stdout
    );
    assert_eq!(
        resolve_console_stream(Some(ConsoleStream::Stderr), Some("stdout")).unwrap(),
        ConsoleStream::Stderr
    );
}

// Launcher argument lists (`LauncherExecutableSet.CreateProbeSpec` / `CreatePlaySpec`).

#[test]
fn launcher_probe_and_headless_play_arguments_parse_as_run() {
    for list in [
        &["run", "--config", "session.json", "--console"][..],
        &["run", "--config", "session.json"][..],
    ] {
        let Ok(Command::Run(command)) = parse_command_line(&args(list)) else {
            panic!("run command expected for {list:?}");
        };
        assert_eq!(command.config_path, "session.json");
    }
}

// OpenAC HeadlessPathSetTests.CommandLineAcceptsRootDir.
#[test]
fn the_command_line_accepts_a_root_folder() {
    let Ok(Command::Run(command)) = parse_command_line(&args(&[
        "run",
        "--config",
        "bot.json",
        "--root-dir",
        "D:/OpenAC",
    ])) else {
        panic!("run command expected");
    };
    assert_eq!(command.paths.root_directory.as_deref(), Some("D:/OpenAC"));
}

#[test]
fn graphical_arguments_are_not_a_headless_command_line() {
    let result = parse_command_line(&args(&["--session-config", "session.json"]));
    assert!(matches!(result, Err(UsageError(_))));
}
