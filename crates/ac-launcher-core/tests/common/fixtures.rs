//! Zip archives and an in-memory web server for update and plugin tests
//! (OpenAC's `UpdateTestData` and `LocalHttpFixture`).
#![allow(dead_code)]

use std::collections::HashMap;
use std::io::Read;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use ac_launcher_core::integrity::sha256_hex;
use ac_launcher_core::net::{HttpError, HttpRequest, HttpResponse, HttpTransport};
use ac_launcher_core::updates::zip::crc32;
use ac_launcher_core::url::WebUrl;

/// How entries are stored.
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Compression {
    /// No compression.
    Stored,
    /// Deflate at the highest level.
    Smallest,
}

/// One entry: name, content, Unix attributes (type and mode).
pub type Entry<'a> = (&'a str, &'a [u8], Option<u32>);

/// Writes a zip archive.
pub fn create_zip(entries: &[Entry<'_>], compression: Compression) -> Vec<u8> {
    let mut out = Vec::new();
    let mut central = Vec::new();
    for (name, content, attributes) in entries {
        let directory = name.ends_with('/');
        let (method, data) = if directory || compression == Compression::Stored {
            (
                0u16,
                if directory {
                    Vec::new()
                } else {
                    content.to_vec()
                },
            )
        } else {
            (8u16, miniz_oxide::deflate::compress_to_vec(content, 10))
        };
        let content: &[u8] = if directory { &[] } else { content };
        let crc = crc32(content);
        let offset = out.len() as u32;
        let flags: u16 = 0x0800;
        out.extend_from_slice(&0x0403_4b50u32.to_le_bytes());
        out.extend_from_slice(&20u16.to_le_bytes());
        out.extend_from_slice(&flags.to_le_bytes());
        out.extend_from_slice(&method.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(&0x21u16.to_le_bytes());
        out.extend_from_slice(&crc.to_le_bytes());
        out.extend_from_slice(&(data.len() as u32).to_le_bytes());
        out.extend_from_slice(&(content.len() as u32).to_le_bytes());
        out.extend_from_slice(&(name.len() as u16).to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(name.as_bytes());
        out.extend_from_slice(&data);

        let made_by: u16 = if attributes.is_some() { 0x0314 } else { 0x0014 };
        central.extend_from_slice(&0x0201_4b50u32.to_le_bytes());
        central.extend_from_slice(&made_by.to_le_bytes());
        central.extend_from_slice(&20u16.to_le_bytes());
        central.extend_from_slice(&flags.to_le_bytes());
        central.extend_from_slice(&method.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0x21u16.to_le_bytes());
        central.extend_from_slice(&crc.to_le_bytes());
        central.extend_from_slice(&(data.len() as u32).to_le_bytes());
        central.extend_from_slice(&(content.len() as u32).to_le_bytes());
        central.extend_from_slice(&(name.len() as u16).to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&(attributes.unwrap_or(0) << 16).to_le_bytes());
        central.extend_from_slice(&offset.to_le_bytes());
        central.extend_from_slice(name.as_bytes());
    }
    let central_offset = out.len() as u32;
    out.extend_from_slice(&central);
    out.extend_from_slice(&0x0605_4b50u32.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out.extend_from_slice(&(entries.len() as u16).to_le_bytes());
    out.extend_from_slice(&(entries.len() as u16).to_le_bytes());
    out.extend_from_slice(&(central.len() as u32).to_le_bytes());
    out.extend_from_slice(&central_offset.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out
}

fn suffix(rid: &str) -> &'static str {
    if rid.starts_with("win-") { ".exe" } else { "" }
}

/// A client payload: the one program and a readme.
pub fn client_zip(rid: &str, marker: &str) -> Vec<u8> {
    let program = format!("openac{}", suffix(rid));
    let content = format!("{marker}-gui");
    create_zip(
        &[
            (&program, content.as_bytes(), Some(0x81ED)),
            ("assets/readme.txt", marker.as_bytes(), Some(0x81A4)),
        ],
        Compression::Stored,
    )
}

/// A launcher payload.
pub fn launcher_zip(rid: &str, marker: &str) -> Vec<u8> {
    let program = format!("openac-launcher{}", suffix(rid));
    let support = format!("support-{marker}");
    create_zip(
        &[
            (&program, marker.as_bytes(), Some(0x81ED)),
            ("support.dat", support.as_bytes(), Some(0x81A4)),
        ],
        Compression::Stored,
    )
}

/// A release manifest naming both payloads.
pub fn manifest(
    version: &str,
    minimum: &str,
    rid: &str,
    client_url: &str,
    client: &[u8],
    launcher_url: &str,
    launcher: &[u8],
) -> Vec<u8> {
    format!(
        r#"{{"schemaVersion":1,"version":"{version}","minimumLauncherVersion":"{minimum}","clients":{{"{rid}":{{"url":"{client_url}","sha256":"{}","size":{}}}}},"launchers":{{"{rid}":{{"url":"{launcher_url}","sha256":"{}","size":{}}}}}}}"#,
        sha256_hex(client),
        client.len(),
        sha256_hex(launcher),
        launcher.len()
    )
    .into_bytes()
}

/// What the fake server answers for one path.
#[derive(Clone)]
pub enum Route {
    /// 200 with a body.
    Body {
        body: Vec<u8>,
        declared_length: Option<u64>,
        chunk_size: usize,
        chunk_delay: Duration,
    },
    /// A redirect.
    Redirect { status: u16, location: String },
    /// A bare status.
    Status(u16),
    /// A transport failure.
    Fail,
}

/// Answers a URL not routed explicitly.
pub type Responder = Arc<dyn Fn(&str, usize) -> Route + Send + Sync>;

/// Runs as each request arrives.
pub type Hook = Arc<dyn Fn(&str) + Send + Sync>;

/// An in-memory web server: routes by full URL, every request logged.
#[derive(Clone, Default)]
pub struct FakeServer {
    routes: Arc<Mutex<HashMap<String, Route>>>,
    requests: Arc<Mutex<Vec<String>>>,
    fallback: Arc<Mutex<Option<Responder>>>,
    on_request: Arc<Mutex<Option<Hook>>>,
}

/// The loopback base every fixture URL uses.
pub const BASE: &str = "http://127.0.0.1:43119/";

impl FakeServer {
    pub fn new() -> Self {
        Self::default()
    }

    /// A URL on the loopback base.
    pub fn url(relative: &str) -> String {
        format!("{BASE}{}", relative.trim_start_matches('/'))
    }

    /// Serves `body` at `url`.
    pub fn add(&self, url: &str, body: &[u8]) {
        self.add_route(
            url,
            Route::Body {
                body: body.to_vec(),
                declared_length: Some(body.len() as u64),
                chunk_size: usize::MAX,
                chunk_delay: Duration::ZERO,
            },
        );
    }

    /// Serves `body` with a declared length that may differ.
    pub fn add_declared(&self, url: &str, body: &[u8], declared_length: Option<u64>) {
        self.add_route(
            url,
            Route::Body {
                body: body.to_vec(),
                declared_length,
                chunk_size: usize::MAX,
                chunk_delay: Duration::ZERO,
            },
        );
    }

    /// Serves `body` slowly.
    pub fn add_slow(&self, url: &str, body: &[u8], chunk_size: usize, chunk_delay: Duration) {
        self.add_route(
            url,
            Route::Body {
                body: body.to_vec(),
                declared_length: Some(body.len() as u64),
                chunk_size,
                chunk_delay,
            },
        );
    }

    pub fn add_route(&self, url: &str, route: Route) {
        let key = WebUrl::parse(url).expect("fixture URL").to_string();
        self.routes.lock().unwrap().insert(key, route);
    }

    /// Answers every unknown URL through `respond(url, request index)`.
    pub fn fallback(&self, respond: impl Fn(&str, usize) -> Route + Send + Sync + 'static) {
        *self.fallback.lock().unwrap() = Some(Arc::new(respond));
    }

    /// Runs `hook` as each request arrives (a test can change the disk mid-install).
    pub fn on_request(&self, hook: impl Fn(&str) + Send + Sync + 'static) {
        *self.on_request.lock().unwrap() = Some(Arc::new(hook));
    }

    /// Every URL requested, in order.
    pub fn requests(&self) -> Vec<String> {
        self.requests.lock().unwrap().clone()
    }

    /// How many requests named `url`.
    pub fn count(&self, url: &str) -> usize {
        let key = WebUrl::parse(url).expect("fixture URL").to_string();
        self.requests().iter().filter(|r| **r == key).count()
    }

    /// The transport the code under test uses.
    pub fn transport(&self) -> Arc<dyn HttpTransport> {
        Arc::new(self.clone())
    }
}

struct SlowBody {
    body: Vec<u8>,
    position: usize,
    chunk_size: usize,
    chunk_delay: Duration,
}

impl Read for SlowBody {
    fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
        if self.position >= self.body.len() {
            return Ok(0);
        }
        if self.position > 0 && !self.chunk_delay.is_zero() {
            std::thread::sleep(self.chunk_delay);
        }
        let count = self
            .chunk_size
            .min(buffer.len())
            .min(self.body.len() - self.position);
        buffer[..count].copy_from_slice(&self.body[self.position..self.position + count]);
        self.position += count;
        Ok(count)
    }
}

impl HttpTransport for FakeServer {
    fn get(
        &self,
        request: &HttpRequest,
        cancel: &ac_launcher_core::cancel::CancelToken,
    ) -> Result<HttpResponse, HttpError> {
        if cancel.is_cancelled() {
            return Err(HttpError::Cancelled);
        }
        let key = request.url.to_string();
        let index = {
            let mut requests = self.requests.lock().unwrap();
            requests.push(key.clone());
            requests.len() - 1
        };
        let hook = self.on_request.lock().unwrap().clone();
        if let Some(hook) = hook {
            hook(&key);
        }
        let route = self.routes.lock().unwrap().get(&key).cloned();
        let route = match route {
            Some(route) => route,
            None => match self.fallback.lock().unwrap().clone() {
                Some(respond) => respond(&key, index),
                None => Route::Status(404),
            },
        };
        let url = request.url.clone();
        match route {
            Route::Body {
                body,
                declared_length,
                chunk_size,
                chunk_delay,
            } => Ok(HttpResponse {
                status: 200,
                location: None,
                content_length: declared_length,
                content_encoding: Vec::new(),
                requested_url: url,
                body: Box::new(SlowBody {
                    body,
                    position: 0,
                    chunk_size,
                    chunk_delay,
                }),
            }),
            Route::Redirect { status, location } => {
                Ok(HttpResponse::redirect(url, status, &location))
            }
            Route::Status(status) => Ok(HttpResponse::with_body(url, status, Vec::new())),
            Route::Fail => Err(HttpError::Failed("Unavailable".into())),
        }
    }
}
