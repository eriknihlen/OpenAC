//! SHA-256 of files and byte streams, written as lowercase hex.

use std::fs::File;
use std::io::{self, Read};
use std::path::Path;

use sha2::{Digest, Sha256};

use crate::cancel::CancelToken;

/// An incremental SHA-256.
#[derive(Debug, Clone, Default)]
pub struct Sha256Hasher(Sha256);

impl Sha256Hasher {
    /// A fresh hash.
    pub fn new() -> Self {
        Self::default()
    }

    /// Adds bytes.
    pub fn update(&mut self, bytes: &[u8]) {
        self.0.update(bytes);
    }

    /// The digest as 64 lowercase hex digits.
    pub fn finish_hex(self) -> String {
        to_hex(&self.0.finalize())
    }
}

fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

/// The SHA-256 of `bytes` as lowercase hex.
pub fn sha256_hex(bytes: &[u8]) -> String {
    to_hex(&Sha256::digest(bytes))
}

/// The SHA-256 of a file as lowercase hex.
pub fn compute_sha256_hex(path: &Path) -> io::Result<String> {
    compute_sha256_hex_cancellable(path, &CancelToken::none())
}

/// The SHA-256 of a file, checking for cancellation between 1 MiB reads.
pub fn compute_sha256_hex_cancellable(path: &Path, cancel: &CancelToken) -> io::Result<String> {
    let mut file = File::open(path)?;
    let mut hasher = Sha256Hasher::new();
    let mut buffer = vec![0u8; 1024 * 1024];
    loop {
        if cancel.is_cancelled() {
            return Err(io::Error::new(
                io::ErrorKind::Interrupted,
                "The operation was cancelled.",
            ));
        }
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hasher.finish_hex())
}

/// Whether two hex digests are equal, ignoring case.
pub fn matches(actual_hex: &str, expected_hex: &str) -> bool {
    actual_hex.eq_ignore_ascii_case(expected_hex)
}

/// Whether a file's SHA-256 equals `expected_hex` (case ignored).
pub fn verify(path: &Path, expected_hex: &str) -> io::Result<bool> {
    Ok(matches(&compute_sha256_hex(path)?, expected_hex))
}
