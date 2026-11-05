//! A loopback HTTP server for the transport tests: every connection is
//! answered by a closure that reads the request head and writes whatever
//! bytes the test wants (OpenAC's `LocalHttpFixture`, but able to misbehave).
#![allow(dead_code)]

use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::Duration;

use ac_launcher_core::url::WebUrl;

/// Answers one connection: the request head as text, and the socket.
pub type Handler = Arc<dyn Fn(&str, &mut TcpStream) + Send + Sync>;

/// A server on `127.0.0.1` and a free port, stopped when dropped.
pub struct LocalHttpServer {
    port: u16,
    stop: Arc<AtomicBool>,
    requests: Arc<Mutex<Vec<String>>>,
    connections: Arc<AtomicUsize>,
    thread: Option<JoinHandle<()>>,
}

/// Reads up to the end of the request head.
fn read_head(stream: &mut TcpStream) -> String {
    let mut head = Vec::new();
    let mut byte = [0u8; 1];
    while !head.ends_with(b"\r\n\r\n") {
        match stream.read(&mut byte) {
            Ok(1) => head.push(byte[0]),
            _ => break,
        }
    }
    String::from_utf8_lossy(&head).into_owned()
}

impl LocalHttpServer {
    /// Serves every connection with `handler` on its own thread.
    pub fn start(handler: impl Fn(&str, &mut TcpStream) + Send + Sync + 'static) -> Self {
        Self::serve(true, Arc::new(handler))
    }

    /// Answers every connection with `answer` at once, without reading a
    /// request (for clients that do not speak HTTP first, such as TLS).
    pub fn raw(answer: Vec<u8>) -> Self {
        Self::serve(
            false,
            Arc::new(move |_, stream| {
                let _ = stream.write_all(&answer);
            }),
        )
    }

    fn serve(read_request: bool, handler: Handler) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind a loopback port");
        listener.set_nonblocking(true).unwrap();
        let port = listener.local_addr().unwrap().port();
        let stop = Arc::new(AtomicBool::new(false));
        let requests = Arc::new(Mutex::new(Vec::new()));
        let connections = Arc::new(AtomicUsize::new(0));
        let thread = {
            let stop = Arc::clone(&stop);
            let requests = Arc::clone(&requests);
            let connections = Arc::clone(&connections);
            std::thread::spawn(move || {
                while !stop.load(Ordering::SeqCst) {
                    match listener.accept() {
                        Ok((mut stream, _)) => {
                            connections.fetch_add(1, Ordering::SeqCst);
                            stream.set_nonblocking(false).unwrap();
                            let handler = Arc::clone(&handler);
                            let requests = Arc::clone(&requests);
                            std::thread::spawn(move || {
                                let head = if read_request {
                                    read_head(&mut stream)
                                } else {
                                    String::new()
                                };
                                requests.lock().unwrap().push(head.clone());
                                handler(&head, &mut stream);
                            });
                        }
                        Err(_) => std::thread::sleep(Duration::from_millis(5)),
                    }
                }
            })
        };
        Self {
            port,
            stop,
            requests,
            connections,
            thread: Some(thread),
        }
    }

    /// Answers every request with `response`, then closes.
    pub fn fixed(response: Vec<u8>) -> Self {
        Self::start(move |_, stream| {
            let _ = stream.write_all(&response);
        })
    }

    /// `http://127.0.0.1:<port><path>`.
    pub fn url(&self, path: &str) -> WebUrl {
        WebUrl::parse(&format!("http://127.0.0.1:{}{path}", self.port)).unwrap()
    }

    /// The request heads received so far.
    pub fn requests(&self) -> Vec<String> {
        self.requests.lock().unwrap().clone()
    }

    /// Connections accepted so far.
    pub fn connections(&self) -> usize {
        self.connections.load(Ordering::SeqCst)
    }
}

impl Drop for LocalHttpServer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

/// The path of a request head's request line.
pub fn path_of(head: &str) -> &str {
    head.split(' ').nth(1).unwrap_or("/")
}

/// A `200 OK` with a `Content-Length` of `declared` and `body`.
pub fn ok_with_length(body: &[u8], declared: u64) -> Vec<u8> {
    let mut response =
        format!("HTTP/1.1 200 OK\r\nContent-Length: {declared}\r\nConnection: close\r\n\r\n")
            .into_bytes();
    response.extend_from_slice(body);
    response
}

/// A `200 OK` with the right `Content-Length`.
pub fn ok(body: &[u8]) -> Vec<u8> {
    ok_with_length(body, body.len() as u64)
}
