//! The local endpoint on Linux and macOS: a Unix-domain socket in a
//! directory only this user may enter (OpenAC: `hub.sock` in the private
//! peer directory).

use std::io::{self, Read, Write};
use std::net::Shutdown;
use std::os::unix::fs::PermissionsExt;
use std::os::unix::net::{UnixListener, UnixStream};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

/// How often an accept with a time limit looks again.
const ACCEPT_POLL: Duration = Duration::from_millis(50);

/// One connected socket. Reads and writes may run on two threads at once;
/// [`Stream::shutdown`] ends both.
pub(super) struct Stream(UnixStream);

impl Stream {
    pub(super) fn read(&self, buffer: &mut [u8]) -> io::Result<usize> {
        (&self.0).read(buffer)
    }

    pub(super) fn write(&self, bytes: &[u8]) -> io::Result<usize> {
        (&self.0).write(bytes)
    }

    pub(super) fn shutdown(&self) {
        let _ = self.0.shutdown(Shutdown::Both);
    }
}

/// Makes `directory` (and its parents), then closes it to everyone but
/// this user.
pub(super) fn prepare(directory: &Path) -> io::Result<()> {
    std::fs::create_dir_all(directory)?;
    std::fs::set_permissions(directory, std::fs::Permissions::from_mode(0o700))
}

pub(super) fn connect(socket: &Path, _timeout: Duration) -> io::Result<Stream> {
    UnixStream::connect(socket).map(Stream)
}

/// The listening socket; its file goes when it does.
pub(super) struct Listener {
    listener: UnixListener,
    path: PathBuf,
}

impl Listener {
    /// Listens at `socket`, replacing a socket file a stopped server left.
    pub(super) fn bind(socket: &Path) -> io::Result<Self> {
        if socket.exists() {
            std::fs::remove_file(socket)?;
        }
        let listener = UnixListener::bind(socket)?;
        listener.set_nonblocking(true)?;
        Ok(Self {
            listener,
            path: socket.to_path_buf(),
        })
    }

    /// The next client, or none when `timeout` passes first.
    pub(super) fn accept(&mut self, timeout: Duration) -> io::Result<Option<Stream>> {
        let deadline = Instant::now() + timeout;
        loop {
            match self.listener.accept() {
                Ok((stream, _)) => {
                    stream.set_nonblocking(false)?;
                    return Ok(Some(Stream(stream)));
                }
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                    if Instant::now() >= deadline {
                        return Ok(None);
                    }
                    std::thread::sleep(ACCEPT_POLL);
                }
                Err(error) => return Err(error),
            }
        }
    }
}

impl Drop for Listener {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.path);
    }
}
