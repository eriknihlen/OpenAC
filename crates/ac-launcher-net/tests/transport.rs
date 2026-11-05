//! The OS-TLS transport against a loopback HTTP server started by each test:
//! the request it sends, how it frames bodies, and how it stops (timeouts,
//! cancellation). The last tests are OpenAC's `ReleaseTransportTests` that
//! ran the downloader over a real socket (`LocalHttpFixture`), here over the
//! real transport too.

mod common;

use std::io::{Read, Write};
use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::integrity::sha256_hex;
use ac_launcher_core::net::{
    self, HttpError, HttpRequest, HttpResponse, HttpTransport, USER_AGENT,
};
use ac_launcher_core::updates::{ReleaseArtifact, UpdateError, VerifiedArtifactDownloader};
use ac_launcher_core::url::WebUrl;
use ac_launcher_net::OsHttpTransport;
use common::{LocalHttpServer, ok, ok_with_length, path_of};

fn transport() -> OsHttpTransport {
    OsHttpTransport::new().expect("the system TLS library")
}

fn get(url: &WebUrl, timeout: Option<Duration>) -> Result<HttpResponse, HttpError> {
    transport().get(
        &HttpRequest {
            url: url.clone(),
            timeout,
        },
        &CancelToken::new(),
    )
}

fn read_all(response: &mut HttpResponse) -> std::io::Result<Vec<u8>> {
    let mut body = Vec::new();
    response.body.read_to_end(&mut body)?;
    Ok(body)
}

#[test]
fn sends_one_plain_get_with_the_launcher_identity() {
    let server = LocalHttpServer::fixed(ok(b"{}"));
    let url = server.url("/releases/latest/download/manifest.json?x=1");
    let mut response = get(&url, None).unwrap();
    assert_eq!(response.status, 200);
    assert_eq!(response.requested_url, url);
    assert_eq!(read_all(&mut response).unwrap(), b"{}");

    let requests = server.requests();
    assert_eq!(requests.len(), 1);
    let head = &requests[0];
    assert!(head.starts_with("GET /releases/latest/download/manifest.json?x=1 HTTP/1.1\r\n"));
    assert!(head.contains(&format!("\r\nUser-Agent: {USER_AGENT}\r\n")));
    assert!(head.contains("\r\nAccept-Encoding: identity\r\n"));
    assert!(head.contains("\r\nConnection: close\r\n"));
    assert!(!head.to_ascii_lowercase().contains("cookie"));
    assert!(!head.to_ascii_lowercase().contains("authorization"));
}

#[test]
fn returns_a_redirect_as_is_without_following_it() {
    let server = LocalHttpServer::fixed(
        b"HTTP/1.1 302 Found\r\nLocation: /download/v2/manifest.json\r\nContent-Length: 0\r\n\r\n"
            .to_vec(),
    );
    let url = server.url("/latest");
    let response = get(&url, None).unwrap();
    assert_eq!(response.status, 302);
    assert_eq!(
        response.location.as_deref(),
        Some("/download/v2/manifest.json")
    );
    assert_eq!(response.requested_url, url);
    assert_eq!(server.connections(), 1);
}

#[test]
fn the_core_walks_redirects_over_the_real_transport() {
    let server = LocalHttpServer::start(|head, stream| {
        let response = match path_of(head) {
            "/latest" => {
                b"HTTP/1.1 302 Found\r\nLocation: /v2/file\r\nContent-Length: 0\r\n\r\n".to_vec()
            }
            "/v2/file" => ok(b"payload"),
            _ => b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n".to_vec(),
        };
        let _ = stream.write_all(&response);
    });
    let mut redirects = Vec::new();
    let mut response = net::send_with_validated_redirects(
        &transport(),
        &server.url("/latest"),
        "test file",
        Some(Duration::from_secs(5)),
        |next| redirects.push(next.to_string()),
        &CancelToken::new(),
    )
    .unwrap();
    assert_eq!(response.status, 200);
    assert_eq!(redirects, [server.url("/v2/file").to_string()]);
    assert_eq!(
        net::read_bounded(&mut response, 1024, "too large", &CancelToken::new()).unwrap(),
        b"payload"
    );
}

#[test]
fn decodes_a_chunked_body_sent_in_pieces_and_reports_no_length() {
    let server = LocalHttpServer::start(|_, stream| {
        let _ = stream.write_all(
            b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 999\r\n\r\n",
        );
        for piece in [
            &b"5\r\nhello\r\n"[..],
            b"1;ext=1\r\n \r\n",
            b"5\r\nworld\r\n0\r\n\r\n",
        ] {
            let _ = stream.write_all(piece);
            let _ = stream.flush();
            std::thread::sleep(Duration::from_millis(20));
        }
    });
    let mut response = get(&server.url("/atom"), None).unwrap();
    assert_eq!(response.content_length, None);
    assert_eq!(read_all(&mut response).unwrap(), b"hello world");
}

#[test]
fn reads_a_body_without_length_until_the_server_closes() {
    let server =
        LocalHttpServer::fixed(b"HTTP/1.0 200 OK\r\n\r\nall the bytes until close".to_vec());
    let mut response = get(&server.url("/"), None).unwrap();
    assert_eq!(response.content_length, None);
    assert_eq!(
        read_all(&mut response).unwrap(),
        b"all the bytes until close"
    );
}

#[test]
fn reports_content_encoding_and_never_decompresses() {
    let raw = b"\x1f\x8b not really gzip";
    let mut answer = format!(
        "HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: {}\r\n\r\n",
        raw.len()
    )
    .into_bytes();
    answer.extend_from_slice(raw);
    let server = LocalHttpServer::fixed(answer);
    let mut response = get(&server.url("/"), None).unwrap();
    assert_eq!(response.content_encoding, ["gzip"]);
    assert_eq!(response.content_length, Some(raw.len() as u64));
    assert_eq!(read_all(&mut response).unwrap(), raw);
}

#[test]
fn a_body_shorter_than_its_declared_length_is_an_error() {
    let server = LocalHttpServer::fixed(ok_with_length(b"short", 50));
    let mut response = get(&server.url("/"), None).unwrap();
    assert_eq!(response.content_length, Some(50));
    let error = read_all(&mut response).unwrap_err();
    assert_eq!(error.kind(), std::io::ErrorKind::UnexpectedEof);
}

#[test]
fn a_server_that_never_answers_times_out_at_the_request_timeout() {
    let server = LocalHttpServer::start(|_, _| std::thread::sleep(Duration::from_secs(5)));
    let started = Instant::now();
    let result = get(&server.url("/"), Some(Duration::from_millis(300)));
    assert_eq!(result.unwrap_err(), HttpError::TimedOut);
    assert!(
        started.elapsed() < Duration::from_secs(2),
        "{:?}",
        started.elapsed()
    );
}

// A body that stalls after the head is cut off by the same deadline: that
// test lives beside the transport (`src/transport.rs`), where the deadline
// can start after the bytes are in the socket. Here the deadline would also
// have to cover a server thread's reply, which on a loaded machine took
// longer than any short deadline.

#[test]
fn cancelling_stops_a_request_waiting_on_a_silent_server() {
    // The cancel is sent once the server holds the whole request (the
    // client is then waiting for a reply that never comes), and the bound
    // runs from the cancel to the return. Timing from before the request
    // also measured how soon a loaded machine scheduled the cancelling
    // thread, which is not the transport's behaviour.
    let server = LocalHttpServer::start(|_, _| std::thread::sleep(Duration::from_secs(5)));
    let cancel = CancelToken::new();
    let (result, cancelled_at, returned_at) = std::thread::scope(|scope| {
        let canceller = cancel.clone();
        let server = &server;
        let timer = scope.spawn(move || {
            let deadline = Instant::now() + Duration::from_secs(30);
            while server.requests().is_empty() && Instant::now() < deadline {
                std::thread::sleep(Duration::from_millis(5));
            }
            let at = Instant::now();
            canceller.cancel();
            at
        });
        let result = transport().get(
            &HttpRequest {
                url: server.url("/"),
                timeout: None,
            },
            &cancel,
        );
        let returned_at = Instant::now();
        (result, timer.join().unwrap(), returned_at)
    });
    assert_eq!(server.requests().len(), 1, "the request reached the server");
    assert_eq!(result.unwrap_err(), HttpError::Cancelled);
    let after_cancel = returned_at.saturating_duration_since(cancelled_at);
    // The server answers after 5 s; well under that means the cancel, not
    // the server, ended the request.
    assert!(after_cancel < Duration::from_secs(2), "{after_cancel:?}");
}

#[test]
fn an_already_cancelled_request_never_connects() {
    let server = LocalHttpServer::fixed(ok(b"x"));
    let cancel = CancelToken::new();
    cancel.cancel();
    let result = transport().get(
        &HttpRequest {
            url: server.url("/"),
            timeout: None,
        },
        &cancel,
    );
    assert_eq!(result.unwrap_err(), HttpError::Cancelled);
    std::thread::sleep(Duration::from_millis(50));
    assert_eq!(server.connections(), 0);
}

#[test]
fn refuses_plain_http_to_another_machine_and_credentials_in_the_address() {
    for address in [
        "http://example.com/manifest.json",
        "ftp://127.0.0.1/file",
        "https://user:secret@127.0.0.1/file",
    ] {
        let result = get(&WebUrl::parse(address).unwrap(), None);
        assert!(
            matches!(result, Err(HttpError::Failed(_))),
            "{address}: {result:?}"
        );
    }
}

#[test]
fn a_refused_connection_is_a_failure() {
    // A port freed before the request could be taken by any other bind in
    // the meantime (another test's server), and the request then reached it.
    // Instead the port stays held on 127.0.0.1 for the whole test, which
    // keeps every wildcard bind off it, and the request goes to the same
    // port on 127.0.0.2: still this machine, but no socket listens there.
    // macOS puts only 127.0.0.1 on the loopback (127.0.0.2 goes unanswered
    // until the timeout), so there it goes to [::1], where no test listens.
    let holder = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let port = holder.local_addr().unwrap().port();
    let host = if cfg!(target_os = "macos") {
        "[::1]"
    } else {
        "127.0.0.2"
    };
    let url = WebUrl::parse(&format!("http://{host}:{port}/")).unwrap();
    let result = get(&url, Some(Duration::from_secs(5)));
    assert!(matches!(result, Err(HttpError::Failed(_))), "{result:?}");
    drop(holder);
}

#[test]
fn a_response_that_is_not_http_is_a_failure() {
    let server = LocalHttpServer::fixed(b"SSH-2.0-OpenSSH_9.6\r\n".to_vec());
    let result = get(&server.url("/"), None);
    assert!(matches!(result, Err(HttpError::Failed(_))), "{result:?}");
}

#[test]
fn https_to_a_plain_server_fails_the_handshake() {
    let server = LocalHttpServer::raw(ok(b"not tls"));
    let url = WebUrl::parse(&server.url("/").to_string().replace("http://", "https://")).unwrap();
    let result = get(&url, Some(Duration::from_secs(5)));
    assert!(matches!(result, Err(HttpError::Failed(_))), "{result:?}");
}

// ---- OpenAC ReleaseTransportTests over the real transport -------------------

fn downloader() -> VerifiedArtifactDownloader {
    VerifiedArtifactDownloader::new(Arc::new(transport()))
}

fn artifact(url: WebUrl, sha256: String, size: u64) -> ReleaseArtifact {
    ReleaseArtifact { url, sha256, size }
}

struct TempDir(std::path::PathBuf);

impl TempDir {
    fn new(name: &str) -> Self {
        let path = std::env::temp_dir().join(format!(
            "openac-launcher-net-{name}-{}",
            ac_launcher_core::ids::unique_hex32()
        ));
        std::fs::create_dir_all(&path).unwrap();
        Self(path)
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// A server sending `body` in `chunk`-byte writes `delay` apart, declaring
/// `declared` bytes.
fn slow_server(body: Vec<u8>, declared: u64, chunk: usize, delay: Duration) -> LocalHttpServer {
    LocalHttpServer::start(move |_, stream| {
        let head = format!("HTTP/1.1 200 OK\r\nContent-Length: {declared}\r\n\r\n");
        if stream.write_all(head.as_bytes()).is_err() {
            return;
        }
        for piece in body.chunks(chunk) {
            if stream
                .write_all(piece)
                .and_then(|()| stream.flush())
                .is_err()
            {
                return;
            }
            if !delay.is_zero() {
                std::thread::sleep(delay);
            }
        }
    })
}

#[test]
fn streams_to_staging_with_progress_and_exact_digest() {
    let root = TempDir::new("download");
    let bytes: Vec<u8> = (0..200_000u32).map(|value| value as u8).collect();
    let server = slow_server(bytes.clone(), bytes.len() as u64, 4096, Duration::ZERO);
    let destination = root.0.join("artifact.zip");
    let mut progress = Vec::new();

    let result = downloader()
        .download_artifact(
            &artifact(
                server.url("/artifact"),
                sha256_hex(&bytes),
                bytes.len() as u64,
            ),
            &destination,
            &mut |value| progress.push(value),
            &CancelToken::new(),
        )
        .unwrap();

    assert_eq!(result.size, bytes.len() as u64);
    assert_eq!(result.sha256, sha256_hex(&bytes));
    assert_eq!(std::fs::read(&destination).unwrap(), bytes);
    assert_eq!(progress[0].bytes_received, 0);
    assert_eq!(progress.last().unwrap().bytes_received, bytes.len() as u64);
}

#[test]
fn wrong_size_header_partial_body_and_hash_delete_staging() {
    for failure in ["short", "header", "hash"] {
        let root = TempDir::new("download-failure");
        let bytes = b"verified bytes".to_vec();
        let expected = if failure == "short" {
            bytes.len() as u64 + 5
        } else {
            bytes.len() as u64
        };
        let declared = if failure == "header" {
            bytes.len() as u64 + 1
        } else {
            expected
        };
        let server = slow_server(bytes.clone(), declared, usize::MAX, Duration::ZERO);
        let destination = root.0.join(format!("{failure}.zip"));
        let hash = if failure == "hash" {
            "0".repeat(64)
        } else {
            sha256_hex(&bytes)
        };
        let result = downloader().download_artifact(
            &artifact(server.url("/artifact"), hash, expected),
            &destination,
            &mut |_| {},
            &CancelToken::new(),
        );
        assert!(
            matches!(result, Err(UpdateError::Failed { .. })),
            "{failure}: {result:?}"
        );
        assert!(!destination.exists(), "{failure}");
    }
}

#[test]
fn cancellation_deletes_partial_staging() {
    let root = TempDir::new("download-cancel");
    let bytes = vec![7u8; 2 * 1024 * 1024];
    let server = slow_server(
        bytes.clone(),
        bytes.len() as u64,
        1024,
        Duration::from_millis(3),
    );
    let destination = root.0.join("cancel.zip");
    let cancel = CancelToken::new();
    let canceller = cancel.clone();
    let timer = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(40));
        canceller.cancel();
    });
    let result = downloader().download_artifact(
        &artifact(server.url("/slow"), sha256_hex(&bytes), bytes.len() as u64),
        &destination,
        &mut |_| {},
        &cancel,
    );
    timer.join().unwrap();
    assert!(result.unwrap_err().is_cancelled());
    assert!(!destination.exists());
}
