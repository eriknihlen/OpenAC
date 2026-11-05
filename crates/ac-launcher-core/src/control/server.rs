//! The control interface's transport: one connection at a time, one framed
//! request each, every connection given [`DEFAULT_REQUEST_TIMEOUT`] (or the
//! timeout the server was started with) from its connect to its reply.

use std::io;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

#[cfg(unix)]
use super::unix_socket::{Endpoint, Stopper, bind};
#[cfg(windows)]
use super::windows_pipe::{Endpoint, Stopper, bind};
use super::{Cancelled, ControlReply, ControlRequest, RequestError};
use crate::cancel::CancelToken;

/// The largest request accepted, in bytes.
pub const MAXIMUM_REQUEST_BYTES: usize = 16 * 1024;

/// How long one connection may take, from its connect to its reply.
pub const DEFAULT_REQUEST_TIMEOUT: Duration = Duration::from_secs(30);

/// Whether `name` may name the endpoint: 1 to 64 ASCII letters, digits,
/// hyphens or underscores.
#[must_use]
pub fn is_valid_pipe_name(name: &str) -> bool {
    (1..=64).contains(&name.len())
        && name
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
}

/// What carries out a request (on its own thread, so a slow one cannot
/// hold the connection past its deadline).
pub type ControlHandle =
    Arc<dyn Fn(&ControlRequest, &CancelToken) -> Result<ControlReply, Cancelled> + Send + Sync>;

/// Where the server reports a connection or itself ending badly.
pub type ReportError = Arc<dyn Fn(&str) + Send + Sync>;

/// The running endpoint; dropping it closes the endpoint and waits for its
/// thread.
pub struct ControlServer {
    stopping: Arc<AtomicBool>,
    stopper: Stopper,
    thread: Option<JoinHandle<()>>,
}

impl std::fmt::Debug for ControlServer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ControlServer").finish_non_exhaustive()
    }
}

impl ControlServer {
    /// Reserves the endpoint `name` (another process holding it is an
    /// error, so two launchers cannot share a name) and serves it on its
    /// own thread.
    ///
    /// # Errors
    /// The name is invalid, taken, or the endpoint cannot be made.
    pub fn start(
        name: &str,
        handle: ControlHandle,
        report_error: ReportError,
        request_timeout: Duration,
    ) -> io::Result<Self> {
        if !is_valid_pipe_name(name) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Use one control pipe name of 1-64 ASCII letters, digits, hyphens or underscores.",
            ));
        }
        let (endpoint, stopper) = bind(name)?;
        let stopping = Arc::new(AtomicBool::new(false));
        let thread = {
            let stopping = Arc::clone(&stopping);
            std::thread::Builder::new()
                .name("launcher-control".into())
                .spawn(move || run(endpoint, &handle, &report_error, request_timeout, &stopping))?
        };
        Ok(Self {
            stopping,
            stopper,
            thread: Some(thread),
        })
    }
}

impl Drop for ControlServer {
    fn drop(&mut self) {
        self.stopping.store(true, Ordering::SeqCst);
        self.stopper.stop();
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

fn run(
    mut endpoint: Endpoint,
    handle: &ControlHandle,
    report_error: &ReportError,
    request_timeout: Duration,
    stopping: &AtomicBool,
) {
    loop {
        match endpoint.accept() {
            Ok(true) => {}
            Ok(false) => return,
            Err(error) => {
                if !stopping.load(Ordering::SeqCst) {
                    report_error(&format!("Launcher control server stopped: {error}."));
                }
                return;
            }
        }
        let deadline = Instant::now() + request_timeout;
        if let Err(error) = serve(&mut endpoint, handle, deadline)
            && !stopping.load(Ordering::SeqCst)
        {
            report_error(&format!(
                "Launcher control connection ended: {:?}. Command outcome may be unknown; \
                 inspect status before retrying.",
                error.kind()
            ));
        }
        endpoint.disconnect();
    }
}

fn serve(endpoint: &mut Endpoint, handle: &ControlHandle, deadline: Instant) -> io::Result<()> {
    let mut prefix = [0u8; 4];
    endpoint.read_exact(&mut prefix, deadline)?;
    let length = i32::from_le_bytes(prefix);
    let reply = match usize::try_from(length) {
        Ok(length) if length > 0 && length <= MAXIMUM_REQUEST_BYTES => {
            let mut payload = vec![0u8; length];
            endpoint.read_exact(&mut payload, deadline)?;
            match ControlRequest::parse(&payload) {
                Ok(request) => call(handle, request, deadline)?,
                Err(RequestError::Empty) => ControlReply::refused("Empty request."),
                Err(RequestError::InvalidJson) => ControlReply::refused("Invalid JSON request."),
            }
        }
        _ => ControlReply::refused("Invalid request length."),
    };
    let response = reply.to_json().into_bytes();
    let length = i32::try_from(response.len())
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "reply too long"))?;
    endpoint.write_all(&length.to_le_bytes(), deadline)?;
    endpoint.write_all(&response, deadline)?;
    endpoint.flush(deadline);
    Ok(())
}

/// Runs the handler on its own thread and waits for it until `deadline`;
/// past it the request is cancelled and the connection ends without a
/// reply (the handler finishes on its own).
fn call(
    handle: &ControlHandle,
    request: ControlRequest,
    deadline: Instant,
) -> io::Result<ControlReply> {
    let cancel = CancelToken::new();
    let (sender, receiver) = mpsc::channel();
    {
        let handle = Arc::clone(handle);
        let cancel = cancel.clone();
        std::thread::Builder::new()
            .name("launcher-control-request".into())
            .spawn(move || {
                let _ = sender.send(handle(&request, &cancel));
            })?;
    }
    let remaining = deadline.saturating_duration_since(Instant::now());
    match receiver.recv_timeout(remaining) {
        Ok(Ok(reply)) => Ok(reply),
        Ok(Err(Cancelled)) => Err(io::Error::new(
            io::ErrorKind::TimedOut,
            "the request was cancelled",
        )),
        Err(_) => {
            cancel.cancel();
            Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "the request outlived its deadline",
            ))
        }
    }
}
