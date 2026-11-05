//! The one door to the network: an HTTP `GET` the host implements, and the
//! launcher's own rules around it (HTTPS only, loopback HTTP for test feeds,
//! every redirect checked before it is followed, bounded bodies).
//!
//! The transport never follows a redirect itself and never decompresses:
//! it returns the status, the few headers the launcher reads, and the body
//! as a stream. The host provides TLS, proxies and timeouts.

use std::collections::HashSet;
use std::io::Read;
use std::time::Duration;

use crate::cancel::CancelToken;
use crate::updates::UpdateError;
use crate::url::WebUrl;

/// How many redirects a request may follow.
pub const MAXIMUM_REDIRECTS: usize = 5;

/// The `User-Agent` every launcher request sends.
pub const USER_AGENT: &str = "OpenAC-launcher/1";

/// One `GET`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HttpRequest {
    /// The address to fetch.
    pub url: WebUrl,
    /// How long the whole request may take, when limited.
    pub timeout: Option<Duration>,
}

/// What came back.
pub struct HttpResponse {
    /// The HTTP status code.
    pub status: u16,
    /// The `Location` header, if any.
    pub location: Option<String>,
    /// The `Content-Length` header, if any.
    pub content_length: Option<u64>,
    /// The `Content-Encoding` values, if any.
    pub content_encoding: Vec<String>,
    /// The address the transport actually requested; differs from the
    /// request only if the transport followed a redirect on its own, which
    /// the launcher refuses.
    pub requested_url: WebUrl,
    /// The body.
    pub body: Box<dyn Read + Send>,
}

impl std::fmt::Debug for HttpResponse {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HttpResponse")
            .field("status", &self.status)
            .field("location", &self.location)
            .field("content_length", &self.content_length)
            .field("content_encoding", &self.content_encoding)
            .field("requested_url", &self.requested_url)
            .finish_non_exhaustive()
    }
}

/// Why a request produced no response.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum HttpError {
    /// Name resolution, connection, TLS or read failure; the text says which.
    #[error("{0}")]
    Failed(String),
    /// The request's own timeout ran out.
    #[error("The request timed out.")]
    TimedOut,
    /// The caller cancelled.
    #[error("The operation was cancelled.")]
    Cancelled,
}

/// A host-supplied HTTP client. It must not follow redirects, must not
/// decompress, must send [`USER_AGENT`], and must not keep cookies.
pub trait HttpTransport: Send + Sync {
    /// Performs one `GET`.
    fn get(&self, request: &HttpRequest, cancel: &CancelToken) -> Result<HttpResponse, HttpError>;
}

/// Refuses anything but HTTPS (or, when allowed, HTTP to this machine).
pub fn require_transport(
    url: &WebUrl,
    description: &str,
    allow_loopback_http: bool,
) -> Result<(), UpdateError> {
    if url.is_https() || (allow_loopback_http && url.is_http() && url.is_loopback()) {
        Ok(())
    } else {
        Err(UpdateError::failed(format!(
            "The {description} URI must use HTTPS{}",
            if allow_loopback_http {
                " (or fixture-only loopback HTTP)."
            } else {
                "."
            }
        )))
    }
}

/// HTTPS, or HTTP to this machine.
pub fn require_secure_or_loopback(url: &WebUrl, description: &str) -> Result<(), UpdateError> {
    require_transport(url, description, true)
}

fn is_redirect(status: u16) -> bool {
    matches!(status, 301 | 302 | 303 | 307 | 308)
}

/// Sends a `GET` and follows redirects, checking each target against the
/// same transport rule before requesting it; loops, more than
/// [`MAXIMUM_REDIRECTS`] hops and a redirect without `Location` are refused.
/// `on_redirect` sees every accepted target.
pub fn send_with_validated_redirects(
    transport: &dyn HttpTransport,
    initial: &WebUrl,
    subject: &str,
    timeout: Option<Duration>,
    mut on_redirect: impl FnMut(&WebUrl),
    cancel: &CancelToken,
) -> Result<HttpResponse, UpdateError> {
    let allow_loopback_http = initial.is_http() && initial.is_loopback();
    let mut current = initial.clone();
    let mut visited = HashSet::new();
    let mut redirects = 0;
    loop {
        require_transport(
            &current,
            &format!("{subject} redirect"),
            allow_loopback_http,
        )?;
        if !visited.insert(current.to_string()) {
            return Err(UpdateError::failed(format!(
                "The {subject} redirect chain contains a loop."
            )));
        }
        cancel.check().map_err(|_| UpdateError::Cancelled)?;
        let response = transport
            .get(
                &HttpRequest {
                    url: current.clone(),
                    timeout,
                },
                cancel,
            )
            .map_err(UpdateError::Transport)?;
        if response.requested_url != current {
            return Err(UpdateError::failed(format!(
                "The {subject} HTTP transport followed an automatic redirect; every redirect \
                 must be validated before it is requested."
            )));
        }
        if !is_redirect(response.status) {
            return Ok(response);
        }
        if redirects >= MAXIMUM_REDIRECTS {
            return Err(UpdateError::failed(format!(
                "The {subject} exceeded {MAXIMUM_REDIRECTS} redirects."
            )));
        }
        let location = response.location.as_deref().ok_or_else(|| {
            UpdateError::failed(format!("The {subject} redirect has no Location header."))
        })?;
        let next = current.join(location).ok_or_else(|| {
            UpdateError::failed(format!("The {subject} redirect Location is not a URI."))
        })?;
        require_transport(&next, &format!("{subject} redirect"), allow_loopback_http)?;
        on_redirect(&next);
        current = next;
        redirects += 1;
    }
}

/// What a failed body read means: the caller's cancellation when the caller
/// cancelled (a transport stops a blocked read by failing it), else the
/// transport's own error when the read carries one (a timeout, say), else a
/// failure with the read's text.
pub fn body_read_error(error: std::io::Error, cancel: &CancelToken) -> HttpError {
    if cancel.is_cancelled() {
        return HttpError::Cancelled;
    }
    match error
        .get_ref()
        .and_then(|inner| inner.downcast_ref::<HttpError>())
    {
        Some(inner) => inner.clone(),
        None => HttpError::Failed(error.to_string()),
    }
}

/// Reads a whole body, refusing more than `maximum` bytes (both from the
/// declared length and from what actually arrives).
pub fn read_bounded(
    response: &mut HttpResponse,
    maximum: u64,
    too_large: &str,
    cancel: &CancelToken,
) -> Result<Vec<u8>, UpdateError> {
    if response
        .content_length
        .is_some_and(|length| length > maximum)
    {
        return Err(UpdateError::failed(too_large));
    }
    let mut output = Vec::new();
    let mut buffer = vec![0u8; 16 * 1024];
    loop {
        cancel.check().map_err(|_| UpdateError::Cancelled)?;
        let read = response
            .body
            .read(&mut buffer)
            .map_err(|error| UpdateError::Transport(body_read_error(error, cancel)))?;
        if read == 0 {
            break;
        }
        if output.len() as u64 + read as u64 > maximum {
            return Err(UpdateError::failed(too_large));
        }
        output.extend_from_slice(&buffer[..read]);
    }
    Ok(output)
}

/// A transport for tests and fixtures: answers from a closure.
pub struct FnTransport<F>(pub F);

impl<F> std::fmt::Debug for FnTransport<F> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("FnTransport")
    }
}

impl<F> HttpTransport for FnTransport<F>
where
    F: Fn(&HttpRequest) -> Result<HttpResponse, HttpError> + Send + Sync,
{
    fn get(&self, request: &HttpRequest, cancel: &CancelToken) -> Result<HttpResponse, HttpError> {
        if cancel.is_cancelled() {
            return Err(HttpError::Cancelled);
        }
        (self.0)(request)
    }
}

impl HttpResponse {
    /// A response with a fixed body (for fixtures).
    pub fn with_body(requested_url: WebUrl, status: u16, body: Vec<u8>) -> Self {
        Self {
            status,
            location: None,
            content_length: Some(body.len() as u64),
            content_encoding: Vec::new(),
            requested_url,
            body: Box::new(std::io::Cursor::new(body)),
        }
    }

    /// A redirect to `location` (for fixtures).
    pub fn redirect(requested_url: WebUrl, status: u16, location: &str) -> Self {
        Self {
            status,
            location: Some(location.to_owned()),
            content_length: Some(0),
            content_encoding: Vec::new(),
            requested_url,
            body: Box::new(std::io::empty()),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_failed_body_read_says_what_stopped_it() {
        let cancel = CancelToken::new();
        let plain = std::io::Error::other("connection reset");
        assert_eq!(
            body_read_error(plain, &cancel),
            HttpError::Failed("connection reset".into())
        );
        let timed_out = std::io::Error::new(std::io::ErrorKind::TimedOut, HttpError::TimedOut);
        assert_eq!(body_read_error(timed_out, &cancel), HttpError::TimedOut);
        cancel.cancel();
        let shut_down = std::io::Error::other("socket shut down");
        assert_eq!(body_read_error(shut_down, &cancel), HttpError::Cancelled);
    }
}
