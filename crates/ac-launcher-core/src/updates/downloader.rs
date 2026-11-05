//! Downloads a payload to a staging file, hashing as it streams, refusing
//! anything larger or different than promised; a failed download leaves no
//! file behind.

use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;

use super::UpdateError;
use super::manifest::{MAXIMUM_ARTIFACT_BYTES, ReleaseArtifact, is_sha256};
use crate::cancel::CancelToken;
use crate::fsutil;
use crate::integrity::{Sha256Hasher, matches};
use crate::net::{self, HttpError, HttpTransport};
use crate::url::WebUrl;

/// Bytes received so far.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ArtifactDownloadProgress {
    /// Bytes written to the staging file.
    pub bytes_received: u64,
    /// The size the progress is measured against.
    pub total_bytes: u64,
}

impl ArtifactDownloadProgress {
    /// Percent done, 0 to 100.
    pub fn percent(&self) -> f64 {
        if self.total_bytes == 0 {
            0.0
        } else {
            (self.bytes_received as f64 * 100.0 / self.total_bytes as f64).clamp(0.0, 100.0)
        }
    }
}

/// A finished, verified download.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VerifiedArtifactDownload {
    /// Where it was written.
    pub file_path: PathBuf,
    /// Its size.
    pub size: u64,
    /// Its SHA-256, lowercase hex.
    pub sha256: String,
}

/// Downloads through the host's transport.
#[derive(Clone)]
pub struct VerifiedArtifactDownloader {
    transport: Arc<dyn HttpTransport>,
}

impl std::fmt::Debug for VerifiedArtifactDownloader {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("VerifiedArtifactDownloader")
    }
}

impl VerifiedArtifactDownloader {
    /// A downloader over `transport`.
    pub fn new(transport: Arc<dyn HttpTransport>) -> Self {
        Self { transport }
    }

    /// Downloads a manifest payload: exactly its size, exactly its hash.
    pub fn download_artifact(
        &self,
        artifact: &ReleaseArtifact,
        destination: &Path,
        progress: &mut dyn FnMut(ArtifactDownloadProgress),
        cancel: &CancelToken,
    ) -> Result<VerifiedArtifactDownload, UpdateError> {
        net::require_secure_or_loopback(&artifact.url, "artifact")?;
        if artifact.size == 0
            || artifact.size > MAXIMUM_ARTIFACT_BYTES
            || !is_sha256(&artifact.sha256)
        {
            return Err(UpdateError::failed(
                "The requested artifact metadata is invalid.",
            ));
        }
        self.download_core(
            &artifact.url,
            &artifact.sha256,
            artifact.size,
            Some(artifact.size),
            destination,
            progress,
            cancel,
        )
    }

    /// Downloads something whose exact size is not known up front, refusing
    /// more than `maximum_bytes`; the hash is the guarantee.
    pub fn download_capped(
        &self,
        url: &WebUrl,
        sha256: &str,
        maximum_bytes: u64,
        destination: &Path,
        progress: &mut dyn FnMut(ArtifactDownloadProgress),
        cancel: &CancelToken,
    ) -> Result<VerifiedArtifactDownload, UpdateError> {
        net::require_secure_or_loopback(url, "artifact")?;
        if maximum_bytes == 0 || maximum_bytes > MAXIMUM_ARTIFACT_BYTES || !is_sha256(sha256) {
            return Err(UpdateError::failed(
                "The requested artifact metadata is invalid.",
            ));
        }
        self.download_core(
            url,
            sha256,
            maximum_bytes,
            None,
            destination,
            progress,
            cancel,
        )
    }

    #[allow(clippy::too_many_arguments)]
    fn download_core(
        &self,
        url: &WebUrl,
        sha256: &str,
        maximum_bytes: u64,
        exact_size: Option<u64>,
        destination: &Path,
        progress: &mut dyn FnMut(ArtifactDownloadProgress),
        cancel: &CancelToken,
    ) -> Result<VerifiedArtifactDownload, UpdateError> {
        let full_path = fsutil::full_path(destination);
        let parent = full_path.parent().ok_or_else(|| {
            UpdateError::failed("The artifact staging path has no parent directory.")
        })?;
        std::fs::create_dir_all(parent).map_err(|error| {
            UpdateError::io(
                format!("The release artifact could not be downloaded: {error}"),
                error,
            )
        })?;
        let mut owns_destination = false;
        let result = (|| {
            let mut response = net::send_with_validated_redirects(
                self.transport.as_ref(),
                url,
                "release artifact",
                None,
                |_| {},
                cancel,
            )?;
            if !(200..300).contains(&response.status) {
                return Err(UpdateError::Transport(HttpError::Failed(format!(
                    "Response status code does not indicate success: {}.",
                    response.status
                ))));
            }
            let declared = response.content_length;
            match (exact_size, declared) {
                (Some(expected), Some(length)) if length != expected => {
                    return Err(UpdateError::failed(format!(
                        "Artifact size header mismatch: expected {expected}, received {length}."
                    )));
                }
                (None, Some(length)) if length > maximum_bytes => {
                    return Err(UpdateError::failed(format!(
                        "Artifact size header {length} exceeds the maximum of {maximum_bytes} \
                         bytes."
                    )));
                }
                _ => {}
            }
            if !response.content_encoding.is_empty() {
                return Err(UpdateError::failed(
                    "Release artifact content encoding is not allowed.",
                ));
            }
            let progress_total = exact_size.or(declared).unwrap_or(maximum_bytes);
            let limit = exact_size.unwrap_or(maximum_bytes);
            let mut output = fsutil::create_new(&full_path, false).map_err(|error| {
                UpdateError::io(
                    format!("The release artifact could not be downloaded: {error}"),
                    error,
                )
            })?;
            owns_destination = true;
            let mut hash = Sha256Hasher::new();
            let mut buffer = vec![0u8; 128 * 1024];
            let mut received: u64 = 0;
            progress(ArtifactDownloadProgress {
                bytes_received: 0,
                total_bytes: progress_total,
            });
            loop {
                cancel.check()?;
                let read = response.body.read(&mut buffer).map_err(|error| {
                    if cancel.is_cancelled() {
                        // The transport failed the read because the caller cancelled.
                        return UpdateError::Cancelled;
                    }
                    UpdateError::io(
                        format!("The release artifact could not be downloaded: {error}"),
                        error,
                    )
                })?;
                if read == 0 {
                    break;
                }
                received += read as u64;
                if received > limit {
                    return Err(UpdateError::failed(match exact_size {
                        Some(declared) => {
                            format!("Artifact exceeded its declared size of {declared} bytes.")
                        }
                        None => format!("Artifact exceeded the maximum of {maximum_bytes} bytes."),
                    }));
                }
                hash.update(&buffer[..read]);
                output.write_all(&buffer[..read]).map_err(|error| {
                    UpdateError::io(
                        format!("The release artifact could not be downloaded: {error}"),
                        error,
                    )
                })?;
                progress(ArtifactDownloadProgress {
                    bytes_received: received,
                    total_bytes: progress_total,
                });
            }
            output
                .flush()
                .and_then(|()| output.sync_all())
                .map_err(|error| {
                    UpdateError::io(
                        format!("The release artifact could not be downloaded: {error}"),
                        error,
                    )
                })?;
            if let Some(expected) = exact_size
                && received != expected
            {
                return Err(UpdateError::failed(format!(
                    "Artifact ended at {received} bytes; expected {expected}."
                )));
            }
            let actual = hash.finish_hex();
            if !matches(&actual, sha256) {
                return Err(UpdateError::failed(if exact_size.is_some() {
                    "Artifact SHA-256 does not match the release manifest."
                } else {
                    "Artifact SHA-256 does not match the expected value."
                }));
            }
            Ok(VerifiedArtifactDownload {
                file_path: full_path.clone(),
                size: received,
                sha256: actual,
            })
        })();
        match result {
            Ok(download) => Ok(download),
            Err(error) => {
                if owns_destination {
                    fsutil::try_delete_file(&full_path);
                }
                Err(match error {
                    UpdateError::Transport(HttpError::Cancelled) => UpdateError::Cancelled,
                    UpdateError::Transport(transport) => UpdateError::failed(format!(
                        "The release artifact could not be downloaded: {transport}"
                    )),
                    other => other,
                })
            }
        }
    }
}
