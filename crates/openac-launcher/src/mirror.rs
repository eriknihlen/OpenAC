//! The GitHub mirror (`OPENAC_LAUNCHER_GITHUB_MIRROR`): a diagnostic that
//! answers every `https://github.com/<path>` request from the file
//! `<folder>/<path>` instead of the network (a missing file is 404), so the
//! plugin pipeline (curated list, releases feed, release assets) can be
//! exercised end to end on one machine without publishing anything. Every
//! other host goes to the real transport. Off unless the variable names a
//! folder.

use std::io::Cursor;
use std::path::PathBuf;
use std::sync::Arc;

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::net::{HttpError, HttpRequest, HttpResponse, HttpTransport};

/// The variable naming the mirror folder.
pub const MIRROR_VARIABLE: &str = "OPENAC_LAUNCHER_GITHUB_MIRROR";

/// A transport answering `github.com` from a folder.
pub struct GitHubMirror {
    root: PathBuf,
    inner: Arc<dyn HttpTransport>,
}

impl GitHubMirror {
    /// Wraps `inner` when [`MIRROR_VARIABLE`] names a folder.
    pub fn wrap_from_environment(inner: Arc<dyn HttpTransport>) -> Arc<dyn HttpTransport> {
        match std::env::var_os(MIRROR_VARIABLE) {
            Some(root) if !root.is_empty() => {
                let root = PathBuf::from(root);
                println!("mirror: github.com is answered from {}", root.display());
                Arc::new(Self { root, inner })
            }
            _ => inner,
        }
    }

    /// The file for a request's path, when every segment is a plain name.
    fn file_for(&self, segments: &[&str]) -> Option<PathBuf> {
        let plain = |segment: &&str| {
            !segment.is_empty()
                && *segment != "."
                && *segment != ".."
                && !segment.contains(['\\', ':', '/'])
        };
        (!segments.is_empty() && segments.iter().all(plain)).then(|| {
            segments
                .iter()
                .fold(self.root.clone(), |path, s| path.join(s))
        })
    }
}

impl HttpTransport for GitHubMirror {
    fn get(&self, request: &HttpRequest, cancel: &CancelToken) -> Result<HttpResponse, HttpError> {
        if !request.url.host().eq_ignore_ascii_case("github.com") {
            return self.inner.get(request, cancel);
        }
        if cancel.is_cancelled() {
            return Err(HttpError::Cancelled);
        }
        let body = self
            .file_for(&request.url.segments())
            .and_then(|path| std::fs::read(path).ok());
        println!(
            "mirror: GET {} -> {}",
            request.url,
            if body.is_some() { 200 } else { 404 }
        );
        let status = if body.is_some() { 200 } else { 404 };
        let body = body.unwrap_or_default();
        Ok(HttpResponse {
            status,
            location: None,
            content_length: Some(body.len() as u64),
            content_encoding: Vec::new(),
            requested_url: request.url.clone(),
            body: Box::new(Cursor::new(body)),
        })
    }
}
