//! The control endpoint on Unix: a local socket where .NET's named-pipe
//! client looks for the pipe `<name>` (`$TMPDIR/CoreFxPipe_<name>`), so
//! the same scripts reach it. The socket is made in a private folder and
//! only then moved into place, readable and writable by the user the
//! launcher runs as and nobody else; a live socket of the same name is
//! another launcher's, and an error.

use std::io::{self, Read, Write};
use std::os::unix::fs::PermissionsExt;
use std::os::unix::net::{UnixListener, UnixStream};
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

fn socket_path(name: &str) -> PathBuf {
    std::env::temp_dir().join(format!("CoreFxPipe_{name}"))
}

pub(super) fn bind(name: &str) -> io::Result<(Endpoint, Stopper)> {
    let path = socket_path(name);
    if UnixStream::connect(&path).is_ok() {
        return Err(io::Error::new(
            io::ErrorKind::AddrInUse,
            format!("another process serves {}", path.display()),
        ));
    }
    match std::fs::remove_file(&path) {
        Ok(()) => {}
        Err(error) if error.kind() == io::ErrorKind::NotFound => {}
        Err(error) => return Err(error),
    }
    let private =
        std::env::temp_dir().join(format!("CoreFxPipe_{name}.{}.tmp", std::process::id()));
    std::fs::create_dir(&private)?;
    let made = (|| {
        std::fs::set_permissions(&private, std::fs::Permissions::from_mode(0o700))?;
        let staged = private.join("socket");
        let listener = UnixListener::bind(&staged)?;
        std::fs::set_permissions(&staged, std::fs::Permissions::from_mode(0o600))?;
        std::fs::rename(&staged, &path)?;
        Ok::<_, io::Error>(listener)
    })();
    let _ = std::fs::remove_dir_all(&private);
    let listener = made?;
    let stopping = Arc::new(AtomicBool::new(false));
    Ok((
        Endpoint {
            listener,
            stream: None,
            stopping: Arc::clone(&stopping),
            path: path.clone(),
        },
        Stopper { stopping, path },
    ))
}

/// Ends the server's waits.
pub(super) struct Stopper {
    stopping: Arc<AtomicBool>,
    path: PathBuf,
}

impl Stopper {
    pub(super) fn stop(&self) {
        self.stopping.store(true, Ordering::SeqCst);
        // Wakes a waiting accept; it then sees the flag.
        let _ = UnixStream::connect(&self.path);
    }
}

/// The socket, serving one client at a time.
pub(super) struct Endpoint {
    listener: UnixListener,
    stream: Option<UnixStream>,
    stopping: Arc<AtomicBool>,
    path: PathBuf,
}

impl Drop for Endpoint {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.path);
    }
}

/// How often a wait looks whether the server is stopping.
const STOP_CHECK: Duration = Duration::from_millis(250);

/// A wait that ended without data and is tried again (a timeout slice, a
/// signal).
fn waits_again(error: &io::Error) -> bool {
    matches!(
        error.kind(),
        io::ErrorKind::Interrupted | io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut
    )
}

fn remaining(deadline: Instant) -> io::Result<Duration> {
    let left = deadline.saturating_duration_since(Instant::now());
    if left.is_zero() {
        Err(io::Error::new(
            io::ErrorKind::TimedOut,
            "the connection outlived its deadline",
        ))
    } else {
        Ok(left)
    }
}

impl Endpoint {
    /// Waits for the next client: `false` once the server is stopped.
    pub(super) fn accept(&mut self) -> io::Result<bool> {
        let (stream, _) = self.listener.accept()?;
        if self.stopping.load(Ordering::SeqCst) {
            return Ok(false);
        }
        self.stream = Some(stream);
        Ok(true)
    }

    /// How long the next wait may be: up to the deadline, in slices short
    /// enough that a stop is seen soon.
    fn slice(&self, deadline: Instant) -> io::Result<Duration> {
        if self.stopping.load(Ordering::SeqCst) {
            return Err(io::Error::new(
                io::ErrorKind::Interrupted,
                "the control server is stopping",
            ));
        }
        Ok(remaining(deadline)?.min(STOP_CHECK))
    }

    fn stream(&mut self) -> io::Result<&mut UnixStream> {
        self.stream
            .as_mut()
            .ok_or_else(|| io::Error::from(io::ErrorKind::NotConnected))
    }

    /// Reads exactly `buffer.len()` bytes before `deadline`.
    pub(super) fn read_exact(&mut self, buffer: &mut [u8], deadline: Instant) -> io::Result<()> {
        let mut filled = 0;
        while filled < buffer.len() {
            let left = self.slice(deadline)?;
            let stream = self.stream()?;
            stream.set_read_timeout(Some(left))?;
            match stream.read(&mut buffer[filled..]) {
                Ok(0) => return Err(io::ErrorKind::UnexpectedEof.into()),
                Ok(count) => filled += count,
                Err(error) if waits_again(&error) => {}
                Err(error) => return Err(error),
            }
        }
        Ok(())
    }

    /// Writes all of `bytes` before `deadline`.
    pub(super) fn write_all(&mut self, bytes: &[u8], deadline: Instant) -> io::Result<()> {
        let mut written = 0;
        while written < bytes.len() {
            let left = self.slice(deadline)?;
            let stream = self.stream()?;
            stream.set_write_timeout(Some(left))?;
            match stream.write(&bytes[written..]) {
                Ok(0) => return Err(io::ErrorKind::WriteZero.into()),
                Ok(count) => written += count,
                Err(error) if waits_again(&error) => {}
                Err(error) => return Err(error),
            }
        }
        Ok(())
    }

    /// A socket keeps what the client has not read after it is closed, so
    /// nothing needs waiting for.
    pub(super) fn flush(&mut self, _deadline: Instant) {}

    /// Ends the current client's connection, if any.
    pub(super) fn disconnect(&mut self) {
        if let Some(stream) = self.stream.take() {
            let _ = stream.shutdown(std::net::Shutdown::Both);
        }
    }
}
