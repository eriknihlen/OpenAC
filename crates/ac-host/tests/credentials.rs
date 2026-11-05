//! Credential providers and secret handling.
//!
//! Ported from OpenAC `tests/AcDream.Headless.Tests/HeadlessCredentialResolverTests.cs`.

mod common;

use std::io::Cursor;

use ac_host::{
    ConfigReader, CredentialProvider, CredentialReference, CredentialResolver, DirectCredentials,
    Os, Secret, load_config, resolve_session_logins,
};
use common::{FixturePlatform, TempDir, configuration_with, session};

fn reference(provider: CredentialProvider, reference: &str) -> CredentialReference {
    CredentialReference {
        provider,
        reference: reference.to_owned(),
    }
}

fn platform() -> FixturePlatform {
    // Not Unix, so the permission check stays out of the way of tests that
    // are not about it.
    FixturePlatform::new(Os::Windows)
}

#[test]
fn environment_secret_is_redacted() {
    const SECRET_VALUE: &str = "test-secret-value";
    let platform = platform().with_var("BOT_PASSWORD", SECRET_VALUE);
    let mut stdin = Cursor::new(Vec::new());
    let mut resolver = CredentialResolver::new(&mut stdin, &std::env::temp_dir(), &platform);

    let secret = resolver
        .resolve(
            "bot",
            &reference(CredentialProvider::Environment, "BOT_PASSWORD"),
        )
        .unwrap();

    assert_eq!(secret.reveal(), SECRET_VALUE);
    assert!(!secret.to_string().contains(SECRET_VALUE));
    assert!(!format!("{secret:?}").contains(SECRET_VALUE));
    assert_eq!(secret.to_string(), "[redacted:BOT_PASSWORD]");
}

#[test]
fn standard_input_consumes_one_secret_without_echoing_it() {
    const SECRET_VALUE: &str = "stdin-secret";
    let platform = platform();
    let mut stdin = Cursor::new(format!("{SECRET_VALUE}\r\nsecond\n").into_bytes());
    let mut resolver = CredentialResolver::new(&mut stdin, &std::env::temp_dir(), &platform);

    let secret = resolver
        .resolve(
            "bot",
            &reference(CredentialProvider::StandardInput, "bot-stdin"),
        )
        .unwrap();
    assert_eq!(secret.reveal(), SECRET_VALUE);
    assert!(!secret.to_string().contains(SECRET_VALUE));

    // One line per reference: the next one reads the next line.
    let next = resolver
        .resolve("two", &reference(CredentialProvider::StandardInput, "two"))
        .unwrap();
    assert_eq!(next.reveal(), "second");
    let error = resolver
        .resolve(
            "three",
            &reference(CredentialProvider::StandardInput, "three"),
        )
        .unwrap_err();
    assert!(error.to_string().contains("unavailable"));
}

#[test]
fn credential_file_is_resolved_relative_to_configured_directory() {
    let directory = TempDir::new("credentials");
    std::fs::write(directory.path.join("bot.pass"), "file-secret\r\n").unwrap();
    owner_only(&directory.path.join("bot.pass"));
    let platform = FixturePlatform::new(Os::current());
    let mut stdin = Cursor::new(Vec::new());
    let mut resolver = CredentialResolver::new(&mut stdin, &directory.path, &platform);

    let secret = resolver
        .resolve("bot", &reference(CredentialProvider::File, "bot.pass"))
        .unwrap();

    assert_eq!(secret.reveal(), "file-secret");
}

#[test]
fn empty_and_missing_credential_files_are_refused() {
    let directory = TempDir::new("credentials-empty");
    std::fs::write(directory.path.join("empty.pass"), "\n").unwrap();
    owner_only(&directory.path.join("empty.pass"));
    let platform = FixturePlatform::new(Os::current());
    let mut stdin = Cursor::new(Vec::new());
    let mut resolver = CredentialResolver::new(&mut stdin, &directory.path, &platform);

    let empty = resolver
        .resolve("bot", &reference(CredentialProvider::File, "empty.pass"))
        .unwrap_err();
    assert!(empty.to_string().contains("is empty"));
    let missing = resolver
        .resolve("bot", &reference(CredentialProvider::File, "missing.pass"))
        .unwrap_err();
    assert!(missing.to_string().contains("could not be resolved"));
}

#[test]
fn missing_secret_error_never_contains_another_secret() {
    const UNRELATED: &str = "must-not-leak";
    let platform = platform().with_var("OTHER", UNRELATED);
    let mut stdin = Cursor::new(Vec::new());
    let mut resolver = CredentialResolver::new(&mut stdin, &std::env::temp_dir(), &platform);

    let error = resolver
        .resolve(
            "bot",
            &reference(CredentialProvider::Environment, "MISSING"),
        )
        .unwrap_err();

    assert!(!error.to_string().contains(UNRELATED));
    assert!(!format!("{error:?}").contains(UNRELATED));
}

#[test]
fn empty_secret_is_refused() {
    assert!(Secret::new("x", String::new()).is_err());
}

#[cfg(unix)]
fn owner_only(path: &std::path::Path) {
    use std::os::unix::fs::PermissionsExt;
    std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o600)).unwrap();
}

#[cfg(not(unix))]
fn owner_only(_path: &std::path::Path) {}

#[cfg(unix)]
#[test]
fn unix_rejects_group_or_other_credential_permissions() {
    use std::os::unix::fs::PermissionsExt;
    let directory = TempDir::new("credentials-mode");
    let path = directory.path.join("shared.pass");
    std::fs::write(&path, "unix-secret").unwrap();
    std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o440)).unwrap();
    let platform = FixturePlatform::new(Os::Linux);
    let mut stdin = Cursor::new(Vec::new());
    let mut resolver = CredentialResolver::new(&mut stdin, &directory.path, &platform);

    let error = resolver
        .resolve("bot", &reference(CredentialProvider::File, "shared.pass"))
        .unwrap_err();
    assert!(error.to_string().contains("readable only by its owner"));
}

#[cfg(unix)]
#[test]
fn symbolic_links_are_refused() {
    let directory = TempDir::new("credentials-link");
    let target = directory.path.join("real.pass");
    std::fs::write(&target, "linked-secret").unwrap();
    owner_only(&target);
    std::os::unix::fs::symlink(&target, directory.path.join("link.pass")).unwrap();
    let platform = FixturePlatform::new(Os::Linux);
    let mut stdin = Cursor::new(Vec::new());
    let mut resolver = CredentialResolver::new(&mut stdin, &directory.path, &platform);

    let error = resolver
        .resolve("bot", &reference(CredentialProvider::File, "link.pass"))
        .unwrap_err();
    assert!(error.to_string().contains("symbolic link"));
}

#[test]
fn session_logins_resolve_in_order_and_direct_credentials_replace_the_account() {
    let config = load_config(
        configuration_with(&[session("bot", "BOT_PASSWORD", None)]).as_bytes(),
        ConfigReader::Headless,
    )
    .unwrap();
    let platform = platform().with_var("BOT_PASSWORD", "from-env");
    let mut stdin = Cursor::new(Vec::new());
    let mut resolver = CredentialResolver::new(&mut stdin, &std::env::temp_dir(), &platform);

    let logins = resolve_session_logins(&config, None, &mut resolver).unwrap();
    assert_eq!(logins[0].session.account, "account");
    assert_eq!(logins[0].password.reveal(), "from-env");

    let direct = DirectCredentials {
        user: "testaccount".into(),
        password: Secret::new("command-line", "typed".into()).unwrap(),
    };
    let logins = resolve_session_logins(&config, Some(direct), &mut resolver).unwrap();
    assert_eq!(logins[0].session.account, "testaccount");
    assert_eq!(logins[0].password.reveal(), "typed");
    assert_eq!(logins[0].password.reference_id(), "command-line");
}
