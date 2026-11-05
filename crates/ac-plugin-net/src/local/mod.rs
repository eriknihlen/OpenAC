//! A local endpoint only this user on this computer can reach: a named
//! pipe on Windows, a Unix-domain socket in a private directory elsewhere
//! (OpenAC `PeerHubEndpoint`). The clients' peer hub runs on it. A stream
//! can be read on one thread while another writes to it, and shutting it
//! down ends both.

use std::io;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

#[cfg(unix)]
mod unix;
#[cfg(unix)]
use unix as imp;
#[cfg(windows)]
mod windows;
#[cfg(windows)]
use windows as imp;

/// Where a local endpoint lives: a private directory (the socket's on
/// Linux and macOS) and a name (the pipe's on Windows).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LocalEndpoint {
    /// The private directory.
    pub directory: PathBuf,
    /// The pipe's name.
    pub name: String,
}

impl LocalEndpoint {
    /// The socket's path on Linux and macOS (a short base name: the
    /// directory already says whose it is, and socket paths are short).
    #[must_use]
    pub fn socket_path(&self) -> PathBuf {
        self.directory.join("hub.sock")
    }

    /// Makes the directory, closed to other users where the system has
    /// such permissions.
    ///
    /// # Errors
    /// The directory cannot be made.
    pub fn prepare(&self) -> io::Result<()> {
        #[cfg(unix)]
        {
            unix::prepare(&self.directory)
        }
        #[cfg(not(unix))]
        {
            std::fs::create_dir_all(&self.directory)
        }
    }

    /// Connects, waiting up to `timeout` while the server is busy. A
    /// missing server is `NotFound` (Windows) or `NotFound` /
    /// `ConnectionRefused` (a socket file left behind).
    ///
    /// # Errors
    /// No server, or the system's error.
    pub fn connect(&self, timeout: Duration) -> io::Result<LocalStream> {
        #[cfg(unix)]
        let stream = imp::connect(&self.socket_path(), timeout)?;
        #[cfg(windows)]
        let stream = imp::connect(&self.name, timeout)?;
        Ok(LocalStream(Arc::new(stream)))
    }

    /// Starts listening.
    ///
    /// # Errors
    /// Another server holds the endpoint, or the system's error.
    pub fn listen(&self) -> io::Result<LocalListener> {
        #[cfg(unix)]
        let listener = imp::Listener::bind(&self.socket_path())?;
        #[cfg(windows)]
        let listener = imp::Listener::bind(&self.name)?;
        Ok(LocalListener(listener))
    }

    /// The lock file the one server of this endpoint holds.
    #[must_use]
    pub fn lock_path(&self) -> PathBuf {
        self.directory.join(format!("{}.lock", self.name))
    }

    /// The directory.
    #[must_use]
    pub fn directory(&self) -> &Path {
        &self.directory
    }
}

/// A connected stream; clones share it.
#[derive(Clone)]
pub struct LocalStream(Arc<imp::Stream>);

impl std::fmt::Debug for LocalStream {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LocalStream").finish_non_exhaustive()
    }
}

impl LocalStream {
    /// Reads what has arrived, waiting for at least one byte; 0 at the
    /// end of the stream.
    ///
    /// # Errors
    /// The stream failed or was shut down.
    pub fn read(&self, buffer: &mut [u8]) -> io::Result<usize> {
        self.0.read(buffer)
    }

    /// Reads exactly `buffer.len()` bytes.
    ///
    /// # Errors
    /// The stream ended or failed first.
    pub fn read_exact(&self, buffer: &mut [u8]) -> io::Result<()> {
        let mut filled = 0;
        while filled < buffer.len() {
            match self.0.read(&mut buffer[filled..])? {
                0 => return Err(io::ErrorKind::UnexpectedEof.into()),
                count => filled += count,
            }
        }
        Ok(())
    }

    /// Writes all of `bytes`.
    ///
    /// # Errors
    /// The stream failed or was shut down.
    pub fn write_all(&self, bytes: &[u8]) -> io::Result<()> {
        let mut written = 0;
        while written < bytes.len() {
            match self.0.write(&bytes[written..])? {
                0 => return Err(io::ErrorKind::WriteZero.into()),
                count => written += count,
            }
        }
        Ok(())
    }

    /// Ends the stream for both directions; a read or write waiting on it
    /// returns.
    pub fn shutdown(&self) {
        self.0.shutdown();
    }
}

/// A listening endpoint.
pub struct LocalListener(imp::Listener);

impl std::fmt::Debug for LocalListener {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LocalListener").finish_non_exhaustive()
    }
}

impl LocalListener {
    /// The next client, or none once `timeout` passes.
    ///
    /// # Errors
    /// The system's error.
    pub fn accept(&mut self, timeout: Duration) -> io::Result<Option<LocalStream>> {
        Ok(self.0.accept(timeout)?.map(|s| LocalStream(Arc::new(s))))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn endpoint(tag: &str) -> LocalEndpoint {
        let directory =
            std::env::temp_dir().join(format!("openac-local-{tag}-{}", std::process::id()));
        LocalEndpoint {
            name: format!("openac-local-test-{tag}-{}", std::process::id()),
            directory,
        }
    }

    // OpenAC `PeerHubEndpoint` / `PeerHubServer`: a client connects, both
    // ends read and write at once (a read waiting on one thread while the
    // other writes), and a shutdown wakes a waiting read.
    #[test]
    fn a_client_and_the_server_talk_both_ways_at_once() {
        let endpoint = endpoint("duplex");
        endpoint.prepare().unwrap();
        let mut listener = endpoint.listen().unwrap();
        assert!(
            listener
                .accept(Duration::from_millis(50))
                .unwrap()
                .is_none()
        );
        let client_endpoint = endpoint.clone();
        let client = std::thread::spawn(move || {
            let stream = client_endpoint.connect(Duration::from_secs(1)).unwrap();
            let reader = stream.clone();
            let waiting = std::thread::spawn(move || {
                let mut buffer = [0u8; 5];
                reader.read_exact(&mut buffer).unwrap();
                buffer
            });
            stream.write_all(b"hello").unwrap();
            let answer = waiting.join().unwrap();
            let mut more = [0u8; 1];
            let blocked = stream.clone();
            let ended = std::thread::spawn(move || blocked.read(&mut more));
            std::thread::sleep(Duration::from_millis(50));
            stream.shutdown();
            (answer, ended.join().unwrap())
        });
        let server = listener
            .accept(Duration::from_secs(5))
            .unwrap()
            .expect("the client");
        let mut buffer = [0u8; 5];
        server.read_exact(&mut buffer).unwrap();
        assert_eq!(&buffer, b"hello");
        server.write_all(b"world").unwrap();
        let (answer, ended) = client.join().unwrap();
        assert_eq!(&answer, b"world");
        assert!(matches!(ended, Ok(0) | Err(_)));
        drop(server);
        drop(listener);
        let _ = std::fs::remove_dir_all(&endpoint.directory);
    }

    // A client that arrives while the server is between two waits for one
    // is let in: the endpoint is there for as long as it listens, not only
    // while `accept` waits. (The hub waits a second at a time; a client
    // turned away backs off for longer each time.)
    #[test]
    fn a_client_arriving_between_two_accepts_is_let_in() {
        let endpoint = endpoint("between");
        endpoint.prepare().unwrap();
        let mut listener = endpoint.listen().unwrap();
        assert!(
            listener
                .accept(Duration::from_millis(20))
                .unwrap()
                .is_none()
        );
        let client = endpoint
            .connect(Duration::from_millis(100))
            .expect("the endpoint is there between two accepts");
        let server = listener
            .accept(Duration::from_secs(5))
            .unwrap()
            .expect("the client that arrived meanwhile");
        server.write_all(b"x").unwrap();
        let mut buffer = [0u8; 1];
        client.read_exact(&mut buffer).unwrap();
        assert_eq!(&buffer, b"x");
        drop(server);
        drop(client);
        drop(listener);
        let _ = std::fs::remove_dir_all(&endpoint.directory);
    }

    #[test]
    fn connecting_to_no_server_is_not_found() {
        let endpoint = endpoint("missing");
        let error = endpoint.connect(Duration::from_millis(100)).unwrap_err();
        assert!(
            matches!(
                error.kind(),
                io::ErrorKind::NotFound | io::ErrorKind::ConnectionRefused
            ),
            "{error:?}"
        );
    }
}
