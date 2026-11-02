//! The launcher's control interface: OpenAC
//! `tests/AcDream.Launcher.Tests/LauncherControlTests.cs`.

use std::path::PathBuf;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, UNIX_EPOCH};

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::control::{
    ControlReply, ControlRequest, ControlServer, LauncherControlHandler, MAXIMUM_REQUEST_BYTES,
};
use ac_launcher_core::installation::LauncherInstallRecord;
use ac_launcher_core::orchestration::{
    LauncherAccountSnapshot, LauncherActivityKind, LauncherActivityState, LauncherCapability,
    LauncherPlatformCapabilities, LauncherServerSnapshot, LauncherSessionSnapshot,
    LauncherStateSnapshot, OrchestratorError, StateChangedListener,
};
use ac_launcher_core::plugins::PluginCatalog;
use ac_launcher_core::profiles::{LaunchMode, LauncherTextEditorKind};
use ac_launcher_core::window::Orchestration;

/// One launch the spy saw: server, account, character, mode.
type Launch = (String, String, Option<String>, LaunchMode);

/// OpenAC `OwnerSpy`: answers only what the handler may ask; anything else
/// is a failure.
struct OwnerSpy {
    account: Mutex<LauncherAccountSnapshot>,
    /// The server's accounts when set; else only `account`.
    accounts: Mutex<Option<Vec<LauncherAccountSnapshot>>>,
    session: Mutex<LauncherSessionSnapshot>,
    launches: Mutex<Vec<Launch>>,
    /// What the account may launch now.
    capability: Mutex<LauncherCapability>,
    console_lines: Mutex<Vec<(String, String)>>,
}

fn spy() -> Arc<OwnerSpy> {
    Arc::new(OwnerSpy {
        account: Mutex::new(LauncherAccountSnapshot {
            server_name: "world".into(),
            account_name: "account".into(),
            characters: Vec::new(),
            has_running_activity: false,
            activity_status: "idle".into(),
            selected_character: Some("Saved character".into()),
            selected_launch_mode: Some(LaunchMode::Headless),
            plugins: Vec::new(),
            profiles: Vec::new(),
            login_commands: vec!["secret-command".into()],
        }),
        accounts: Mutex::new(None),
        session: Mutex::new(LauncherSessionSnapshot {
            session_id: "session".into(),
            kind: LauncherActivityKind::Play,
            server_name: "world".into(),
            account_name: "account".into(),
            character_name: Some("Saved character".into()),
            launch_mode: Some(LaunchMode::Headless),
            state: LauncherActivityState::InWorld,
            status: String::new(),
            exit_code: None,
            error: None,
            created_at: UNIX_EPOCH,
            exit_reason: None,
            exited_gracefully: false,
            server_message: None,
            account_in_use: false,
            stop: None,
            not_responding: false,
            plugin_notice: None,
            has_live_process: None,
            startup_in_flight: false,
            window: Default::default(),
        }),
        launches: Mutex::new(Vec::new()),
        capability: Mutex::new(LauncherCapability::AVAILABLE),
        console_lines: Mutex::new(Vec::new()),
    })
}

fn unexpected(call: &str) -> ! {
    panic!("Unexpected owner call: {call}")
}

impl Orchestration for OwnerSpy {
    fn subscribe(&self, _listener: StateChangedListener) {
        unexpected("subscribe")
    }
    fn load_profiles(&self) -> Result<(), OrchestratorError> {
        unexpected("load_profiles")
    }
    fn snapshot(&self) -> Result<LauncherStateSnapshot, OrchestratorError> {
        Ok(LauncherStateSnapshot {
            servers: vec![LauncherServerSnapshot {
                name: "world".into(),
                host: "localhost".into(),
                port: 9000,
                accounts: self
                    .accounts
                    .lock()
                    .unwrap()
                    .clone()
                    .unwrap_or_else(|| vec![self.account.lock().unwrap().clone()]),
            }],
            sessions: vec![self.session.lock().unwrap().clone()],
            platform: LauncherPlatformCapabilities::for_operating_system(true, false, false),
            is_installation_ready: true,
            installation_status: "ready".into(),
            show_beta_plugins: false,
        })
    }
    fn launch_capability(
        &self,
        _mode: LaunchMode,
    ) -> Result<LauncherCapability, OrchestratorError> {
        unexpected("launch_capability")
    }
    fn account_launch_capability(
        &self,
        _server_name: &str,
        _account_name: &str,
        _mode: LaunchMode,
    ) -> Result<LauncherCapability, OrchestratorError> {
        Ok(self.capability.lock().unwrap().clone())
    }
    fn set_install_record(
        &self,
        _record: Option<LauncherInstallRecord>,
    ) -> Result<(), OrchestratorError> {
        unexpected("set_install_record")
    }
    fn set_installation_state(
        &self,
        _record: Option<LauncherInstallRecord>,
        _status: &str,
    ) -> Result<(), OrchestratorError> {
        unexpected("set_installation_state")
    }
    fn set_plugin_catalog(&self, _catalog: Option<PluginCatalog>) -> Result<(), OrchestratorError> {
        unexpected("set_plugin_catalog")
    }
    fn set_show_beta_plugins(&self, _value: bool) -> Result<(), OrchestratorError> {
        unexpected("set_show_beta_plugins")
    }
    fn read_profile_text(
        &self,
        _kind: LauncherTextEditorKind,
    ) -> Result<String, OrchestratorError> {
        unexpected("read_profile_text")
    }
    fn save_profile_text(
        &self,
        _kind: LauncherTextEditorKind,
        _text: &str,
        _original_text: &str,
    ) -> Result<(), OrchestratorError> {
        unexpected("save_profile_text")
    }
    fn describe_profile_text_removals(
        &self,
        _kind: LauncherTextEditorKind,
        _text: &str,
    ) -> Result<Vec<String>, OrchestratorError> {
        unexpected("describe_profile_text_removals")
    }
    fn update_character_settings(
        &self,
        _server_name: &str,
        _account_name: &str,
        _character_name: &str,
        _launch_mode: LaunchMode,
        _plugins: Option<&[String]>,
    ) -> Result<(), OrchestratorError> {
        unexpected("update_character_settings")
    }
    fn update_account_plugins(
        &self,
        _server_name: &str,
        _account_name: &str,
        _plugins: &[String],
    ) -> Result<(), OrchestratorError> {
        unexpected("update_account_plugins")
    }
    fn remove_character(
        &self,
        _server_name: &str,
        _account_name: &str,
        _character_name: &str,
    ) -> Result<(), OrchestratorError> {
        unexpected("remove_character")
    }
    fn add_server(&self, _name: &str, _host: &str, _port: i32) -> Result<(), OrchestratorError> {
        unexpected("add_server")
    }
    fn take_profile_migration_notice(&self) -> Result<Option<String>, OrchestratorError> {
        unexpected("take_profile_migration_notice")
    }
    fn update_account_selection(
        &self,
        _server_name: &str,
        _account_name: &str,
        _selected_character: Option<&str>,
        _selected_launch_mode: LaunchMode,
    ) -> Result<(), OrchestratorError> {
        unexpected("update_account_selection")
    }
    fn launch(
        &self,
        server_name: &str,
        account_name: &str,
        character_name: Option<&str>,
        mode: LaunchMode,
        _cancel: &CancelToken,
    ) -> Result<LauncherSessionSnapshot, OrchestratorError> {
        self.launches.lock().unwrap().push((
            server_name.into(),
            account_name.into(),
            character_name.map(str::to_owned),
            mode,
        ));
        Ok(self.session.lock().unwrap().clone())
    }
    fn stop_session(
        &self,
        _session_id: &str,
        _timeout: Duration,
        _cancel: &CancelToken,
    ) -> Result<(), OrchestratorError> {
        unexpected("stop_session (a forced stop)")
    }
    fn poll_status(&self) -> Result<(), OrchestratorError> {
        Ok(())
    }
    fn try_send_console_line(&self, session_id: &str, line: &str) -> bool {
        self.console_lines
            .lock()
            .unwrap()
            .push((session_id.into(), line.into()));
        true
    }
    fn set_session_window(&self, _session_id: &str, _open: bool) -> Result<(), OrchestratorError> {
        unexpected("set_session_window")
    }
    fn session_log_path(&self, _session_id: &str) -> Option<PathBuf> {
        unexpected("session_log_path")
    }
    fn close_to_new_sessions(&self) -> Result<(), OrchestratorError> {
        unexpected("close_to_new_sessions")
    }
    fn dispose(&self) {
        unexpected("dispose")
    }
}

fn handler(owner: &Arc<OwnerSpy>) -> LauncherControlHandler {
    LauncherControlHandler::new(Arc::clone(owner) as Arc<dyn Orchestration>, None, None)
}

fn request(
    command: &str,
    server: Option<&str>,
    account: Option<&str>,
    session: Option<&str>,
) -> ControlRequest {
    ControlRequest {
        command: Some(command.into()),
        server: server.map(str::to_owned),
        account: account.map(str::to_owned),
        session: session.map(str::to_owned),
    }
}

fn ok(handler: &LauncherControlHandler, request: &ControlRequest) -> bool {
    handler.handle(request, &CancelToken::new()).unwrap().ok
}

// OpenAC StartsSavedSelectionAndRejectsDuplicateOrGraphicalLaunch.
#[test]
fn starts_the_saved_selection_and_rejects_a_duplicate_or_graphical_launch() {
    let owner = spy();
    let handler = handler(&owner);
    let start = request("start", Some("world"), Some("account"), None);
    assert!(ok(&handler, &start));
    assert_eq!(
        *owner.launches.lock().unwrap(),
        [(
            "world".to_owned(),
            "account".to_owned(),
            Some("Saved character".to_owned()),
            LaunchMode::Headless
        )]
    );
    owner.account.lock().unwrap().has_running_activity = true;
    assert!(!ok(&handler, &start));
    {
        let mut account = owner.account.lock().unwrap();
        account.has_running_activity = false;
        account.selected_launch_mode = Some(LaunchMode::Gui);
    }
    assert!(!ok(&handler, &start));
    assert!(!ok(
        &handler,
        &request("start", Some("other"), Some("account"), None)
    ));
    assert_eq!(owner.launches.lock().unwrap().len(), 1);
}

// Not an OpenAC test: a start while the server may still hold the account
// is refused with the seconds left, and nothing is launched.
#[test]
fn start_obeys_the_reconnect_hold() {
    let owner = spy();
    let handler = handler(&owner);
    *owner.capability.lock().unwrap() =
        LauncherCapability::held(std::time::Duration::from_millis(24_200));
    let reply = handler
        .handle(
            &request("start", Some("world"), Some("account"), None),
            &CancelToken::new(),
        )
        .unwrap();
    assert!(!reply.ok);
    assert_eq!(
        reply.error.as_deref(),
        Some("The server may still hold this account. Ready in 25 s.")
    );
    assert!(owner.launches.lock().unwrap().is_empty());
}

// OpenAC StartFindsTheSavedAccountWhateverTheCaseOfItsName.
#[test]
fn start_finds_the_saved_account_whatever_the_case_of_its_name() {
    let owner = spy();
    let handler = handler(&owner);
    assert!(ok(
        &handler,
        &request("start", Some("world"), Some("ACCOUNT"), None)
    ));
    assert_eq!(owner.launches.lock().unwrap()[0].1, "account");

    // A file saved before names were compared without case can hold two
    // spellings: a name spelled neither way starts the first.
    {
        let first = owner.account.lock().unwrap().clone();
        let mut second = first.clone();
        second.account_name = "Account".into();
        *owner.accounts.lock().unwrap() = Some(vec![first, second]);
    }
    assert!(ok(
        &handler,
        &request("start", Some("world"), Some("ACCOUNT"), None)
    ));
    assert_eq!(owner.launches.lock().unwrap()[1].1, "account");
    assert!(ok(
        &handler,
        &request("start", Some("world"), Some("Account"), None)
    ));
    assert_eq!(owner.launches.lock().unwrap()[2].1, "Account");
    assert!(!ok(
        &handler,
        &request("start", Some("WORLD"), Some("account"), None)
    ));
}

// OpenAC StopRequestsQuitWithoutForceStopAndStatusOmitsCommands.
#[test]
fn stop_requests_quit_without_a_forced_stop_and_status_omits_commands() {
    let owner = spy();
    let handler = handler(&owner);
    assert!(ok(&handler, &request("stop", None, None, Some("session"))));
    assert_eq!(
        *owner.console_lines.lock().unwrap(),
        [("session".to_owned(), "/quit".to_owned())]
    );
    assert!(!ok(&handler, &request("stop", None, None, Some("missing"))));
    let status = handler
        .handle(&ControlRequest::command("status"), &CancelToken::new())
        .unwrap()
        .to_json();
    assert!(!status.contains("secret-command"), "{status}");
    assert!(
        !status.to_ascii_lowercase().contains("logincommands"),
        "{status}"
    );
    {
        let mut session = owner.session.lock().unwrap();
        session.state = LauncherActivityState::Exited;
        session.exit_code = Some(0);
        session.exited_gracefully = true;
    }
    assert!(ok(&handler, &request("stop", None, None, Some("session"))));
    assert_eq!(owner.console_lines.lock().unwrap().len(), 1);
}

#[test]
fn status_names_the_saved_selections_and_sessions_as_openac_does() {
    let owner = spy();
    let handler = LauncherControlHandler::new(
        Arc::clone(&owner) as Arc<dyn Orchestration>,
        None,
        Some(Arc::new(|| Some("0.1.20+local".to_owned()))),
    );
    let reply = handler
        .handle(&ControlRequest::command("status"), &CancelToken::new())
        .unwrap();
    let node = ac_host::json::parse(reply.to_json().as_bytes()).unwrap();
    let text = |path: &[&str]| -> String {
        let mut at = &node;
        for name in path {
            let ac_host::json::Value::Object(members) = &at.value else {
                panic!("{name}")
            };
            if let Ok(index) = name.parse::<usize>() {
                at = &members[index].value;
                continue;
            }
            at = &members.iter().find(|m| m.name == *name).unwrap().value;
        }
        match &at.value {
            ac_host::json::Value::String(text) => text.clone(),
            ac_host::json::Value::Bool(b) => b.to_string(),
            other => format!("{other:?}"),
        }
    };
    assert_eq!(text(&["ok"]), "true");
    assert_eq!(text(&["data", "ready"]), "true");
    assert_eq!(text(&["data", "clientVersion"]), "0.1.20+local");
    let ac_host::json::Value::Object(data) = &node.value else {
        panic!()
    };
    let data = &data.iter().find(|m| m.name == "data").unwrap().value;
    let ac_host::json::Value::Object(data) = &data.value else {
        panic!()
    };
    let array = |name: &str| match &data.iter().find(|m| m.name == name).unwrap().value.value {
        ac_host::json::Value::Array(items) => items.clone(),
        _ => panic!("{name}"),
    };
    let field = |item: &ac_host::json::Node, name: &str| match &item.value {
        ac_host::json::Value::Object(members) => members
            .iter()
            .find(|m| m.name == name)
            .unwrap()
            .value
            .value
            .clone(),
        _ => panic!(),
    };
    let accounts = array("accounts");
    assert_eq!(accounts.len(), 1);
    assert_eq!(
        field(&accounts[0], "mode"),
        ac_host::json::Value::String("Headless".into())
    );
    assert_eq!(
        field(&accounts[0], "character"),
        ac_host::json::Value::String("Saved character".into())
    );
    let sessions = array("sessions");
    assert_eq!(
        field(&sessions[0], "state"),
        ac_host::json::Value::String("InWorld".into())
    );
    assert_eq!(
        field(&sessions[0], "session"),
        ac_host::json::Value::String("session".into())
    );
    assert_eq!(
        field(&sessions[0], "active"),
        ac_host::json::Value::Bool(true)
    );
    assert_eq!(
        field(&sessions[0], "graceful"),
        ac_host::json::Value::Bool(false)
    );
    assert_eq!(field(&sessions[0], "exitCode"), ac_host::json::Value::Null);
}

// OpenAC ClientReloadRefusesActiveSessionsAndUsesVerifierWhenIdle.
#[test]
fn client_reload_refuses_active_sessions_and_uses_the_verifier_when_idle() {
    let owner = spy();
    let reloads = Arc::new(AtomicUsize::new(0));
    let counted = Arc::clone(&reloads);
    let handler = LauncherControlHandler::new(
        Arc::clone(&owner) as Arc<dyn Orchestration>,
        Some(Arc::new(move |_: &CancelToken| {
            counted.fetch_add(1, Ordering::SeqCst);
            ControlReply::empty()
        })),
        None,
    );
    let reload = ControlRequest::command("reload-client");
    assert!(!ok(&handler, &reload));
    assert_eq!(reloads.load(Ordering::SeqCst), 0);
    owner.session.lock().unwrap().state = LauncherActivityState::Exited;
    assert!(ok(&handler, &reload));
    assert_eq!(reloads.load(Ordering::SeqCst), 1);
}

// OpenAC ExpiredRequestCannotStopAClient.
#[test]
fn an_expired_request_cannot_stop_a_client() {
    let owner = spy();
    let handler = handler(&owner);
    let expired = CancelToken::new();
    expired.cancel();
    assert!(
        handler
            .handle(&request("stop", None, None, Some("session")), &expired)
            .is_err()
    );
    assert!(owner.console_lines.lock().unwrap().is_empty());
}

#[test]
fn an_unknown_command_names_the_commands() {
    let owner = spy();
    let reply = handler(&owner)
        .handle(&ControlRequest::command("dance"), &CancelToken::new())
        .unwrap();
    assert_eq!(
        reply.error.as_deref(),
        Some("Unknown command. Use status, start, stop or reload-client.")
    );
}

// --- The transport ----------------------------------------------------------------------------

/// Short: the socket is staged at `$TMPDIR/CoreFxPipe_<name>.<pid>.tmp/socket`, and macOS's
/// per-user temp folder is long while its socket paths stop at 104 bytes.
fn pipe_name() -> String {
    static NEXT: AtomicUsize = AtomicUsize::new(0);
    format!(
        "lt-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )
}

#[cfg(windows)]
fn connect(name: &str) -> std::fs::File {
    let path = format!(r"\\.\pipe\{name}");
    let end = Instant::now() + Duration::from_secs(5);
    loop {
        match std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .open(&path)
        {
            Ok(file) => return file,
            // The one instance is busy with the previous client.
            Err(_) if Instant::now() < end => std::thread::sleep(Duration::from_millis(10)),
            Err(error) => panic!("{error}"),
        }
    }
}

#[cfg(unix)]
fn connect(name: &str) -> std::os::unix::net::UnixStream {
    std::os::unix::net::UnixStream::connect(std::env::temp_dir().join(format!("CoreFxPipe_{name}")))
        .unwrap()
}

/// One request and its reply; `None` when the server closed the
/// connection without one.
fn exchange(name: &str, payload: &[u8], declared: Option<i32>) -> Option<ac_host::json::Node> {
    use std::io::{Read, Write};
    let mut client = connect(name);
    let length = declared.unwrap_or_else(|| i32::try_from(payload.len()).unwrap());
    client.write_all(&length.to_le_bytes()).unwrap();
    if !payload.is_empty() {
        client.write_all(payload).unwrap();
    }
    let mut prefix = [0u8; 4];
    client.read_exact(&mut prefix).ok()?;
    let mut response = vec![0u8; usize::try_from(i32::from_le_bytes(prefix)).unwrap()];
    client.read_exact(&mut response).ok()?;
    Some(ac_host::json::parse(&response).unwrap())
}

fn reply_ok(node: &ac_host::json::Node) -> bool {
    let ac_host::json::Value::Object(members) = &node.value else {
        panic!()
    };
    members.iter().find(|m| m.name == "ok").unwrap().value.value == ac_host::json::Value::Bool(true)
}

// OpenAC PipeHandlesFramingMalformedRequestsAndCancellation.
#[test]
fn the_pipe_handles_framing_malformed_requests_and_cancellation() {
    let name = pipe_name();
    let calls = Arc::new(AtomicUsize::new(0));
    let counted = Arc::clone(&calls);
    let server = ControlServer::start(
        &name,
        Arc::new(move |request: &ControlRequest, _: &CancelToken| {
            counted.fetch_add(1, Ordering::SeqCst);
            Ok(ControlReply::done(ac_host::json::Value::String(
                request.command.clone().unwrap_or_default(),
            )))
        }),
        Arc::new(|_: &str| {}),
        Duration::from_secs(30),
    )
    .unwrap();
    assert!(reply_ok(
        &exchange(&name, br#"{"command":"status"}"#, None).unwrap()
    ));
    assert!(!reply_ok(&exchange(&name, b"{", None).unwrap()));
    let too_long = i32::try_from(MAXIMUM_REQUEST_BYTES + 1).unwrap();
    assert!(!reply_ok(&exchange(&name, b"", Some(too_long)).unwrap()));
    assert!(reply_ok(
        &exchange(&name, br#"{"Command":"status"}"#, None).unwrap()
    ));
    assert_eq!(calls.load(Ordering::SeqCst), 2);
    let started = Instant::now();
    drop(server);
    assert!(started.elapsed() < Duration::from_secs(5));
}

// OpenAC DeadlineReleasesConnectionEvenWhenHandlerHasNotReturned.
#[test]
fn the_deadline_releases_the_connection_even_when_the_handler_has_not_returned() {
    let name = pipe_name();
    let (release, pending) = std::sync::mpsc::channel::<()>();
    let pending = Mutex::new(pending);
    let _server = ControlServer::start(
        &name,
        Arc::new(move |request: &ControlRequest, _: &CancelToken| {
            if request.command.as_deref() == Some("pending") {
                let _ = pending.lock().unwrap().recv();
            }
            Ok(ControlReply::empty())
        }),
        Arc::new(|_: &str| {}),
        Duration::from_millis(200),
    )
    .unwrap();
    assert!(exchange(&name, br#"{"command":"pending"}"#, None).is_none());
    assert!(reply_ok(
        &exchange(&name, br#"{"command":"status"}"#, None).unwrap()
    ));
    let _ = release.send(());
}

#[test]
fn a_second_server_cannot_take_a_name_in_use() {
    let name = pipe_name();
    let handle = || -> ac_launcher_core::control::ControlHandle {
        Arc::new(|_: &ControlRequest, _: &CancelToken| Ok(ControlReply::empty()))
    };
    let _first = ControlServer::start(
        &name,
        handle(),
        Arc::new(|_: &str| {}),
        Duration::from_secs(1),
    )
    .unwrap();
    assert!(
        ControlServer::start(
            &name,
            handle(),
            Arc::new(|_: &str| {}),
            Duration::from_secs(1)
        )
        .is_err()
    );
}
