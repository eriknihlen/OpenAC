//! A size-bounded log of a child's output (`client.log`) that
//! keeps the most recent output: at most 2 MiB per file by default, then the
//! file rotates to `client.log.1` (older generations shifting up to
//! `.3`, the oldest dropped). A write failure turns the capture off rather
//! than failing the session.

use std::fs::OpenOptions;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use crate::fsutil;

/// The default size of one file.
pub const DEFAULT_MAX_BYTES: u64 = 2 * 1024 * 1024;
/// The default number of files kept (the current one and its backups).
pub const DEFAULT_MAX_FILES: u32 = 4;

#[derive(Debug, Default)]
struct CaptureState {
    directory_ensured: bool,
    written: u64,
    latched_off: bool,
    disposed: bool,
}

/// The bounded, rotating log.
#[derive(Debug)]
pub struct BoundedProcessOutputCapture {
    path: PathBuf,
    max_bytes: u64,
    max_files: u32,
    state: Mutex<CaptureState>,
}

/// Why a capture could not be made.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum CaptureError {
    /// The path is blank.
    #[error("The capture path cannot be empty or white space.")]
    BlankPath,
    /// The size is zero.
    #[error("The bounded capture size must be positive.")]
    NonPositiveCap,
    /// No file would be kept.
    #[error("The bounded capture must keep at least one file.")]
    NonPositiveFileCount,
}

impl BoundedProcessOutputCapture {
    /// A capture writing to `path`, `max_bytes` per file, keeping
    /// `max_files` files.
    pub fn new(path: &Path, max_bytes: u64, max_files: u32) -> Result<Self, CaptureError> {
        if path.to_string_lossy().trim().is_empty() {
            return Err(CaptureError::BlankPath);
        }
        if max_bytes == 0 {
            return Err(CaptureError::NonPositiveCap);
        }
        if max_files == 0 {
            return Err(CaptureError::NonPositiveFileCount);
        }
        Ok(Self {
            path: fsutil::full_path(path),
            max_bytes,
            max_files,
            state: Mutex::new(CaptureState::default()),
        })
    }

    /// A capture with the default 2 MiB files, four of them.
    pub fn with_default_cap(path: &Path) -> Result<Self, CaptureError> {
        Self::new(path, DEFAULT_MAX_BYTES, DEFAULT_MAX_FILES)
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, CaptureState> {
        self.state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    /// Whether nothing more will be written (failed or closed).
    pub fn is_done(&self) -> bool {
        let state = self.lock();
        state.latched_off || state.disposed
    }

    /// Appends `line` and a newline; `None` (end of stream) does nothing.
    pub fn append_line(&self, line: Option<&str>) {
        let Some(line) = line else {
            return;
        };
        let mut buffer = line.as_bytes().to_vec();
        buffer.push(b'\n');
        let mut state = self.lock();
        self.append_locked(&mut state, &buffer);
    }

    /// Appends raw bytes (no line boundary implied).
    pub fn append(&self, data: &[u8]) {
        if data.is_empty() {
            return;
        }
        let mut state = self.lock();
        self.append_locked(&mut state, data);
    }

    /// Stops all further writes.
    pub fn close(&self) {
        self.lock().disposed = true;
    }

    fn append_locked(&self, state: &mut CaptureState, data: &[u8]) {
        if data.is_empty() || state.disposed || state.latched_off {
            return;
        }
        if self.write_rotating(state, data).is_err() {
            state.latched_off = true;
        }
    }

    fn write_rotating(&self, state: &mut CaptureState, mut data: &[u8]) -> std::io::Result<()> {
        while !data.is_empty() {
            let length = data.len() as u64;
            let mut remaining = self.max_bytes - state.written;
            // A line or one read of the error stream stays in one file when
            // it fits in one; a longer write is split across files without
            // losing bytes.
            if remaining == 0
                || (state.written > 0 && length <= self.max_bytes && length > remaining)
            {
                self.rotate(state)?;
                remaining = self.max_bytes;
            }
            let to_write = length.min(remaining) as usize;
            self.write_chunk(state, &data[..to_write])?;
            state.written += to_write as u64;
            data = &data[to_write..];
        }
        Ok(())
    }

    fn ensure_directory(&self, state: &mut CaptureState) -> std::io::Result<()> {
        if !state.directory_ensured {
            if let Some(directory) = self.path.parent() {
                std::fs::create_dir_all(directory)?;
            }
            state.directory_ensured = true;
        }
        Ok(())
    }

    fn write_chunk(&self, state: &mut CaptureState, chunk: &[u8]) -> std::io::Result<()> {
        self.ensure_directory(state)?;
        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.path)?;
        file.write_all(chunk)?;
        file.flush()
    }

    /// Shifts each file one generation up (the oldest is replaced) and
    /// starts the current file empty.
    fn rotate(&self, state: &mut CaptureState) -> std::io::Result<()> {
        self.ensure_directory(state)?;
        if self.max_files == 1 {
            remove_if_present(&self.path)?;
        } else {
            for generation in (1..self.max_files).rev() {
                let source = if generation == 1 {
                    self.path.clone()
                } else {
                    self.backup_path(generation - 1)
                };
                if source.exists() {
                    std::fs::rename(&source, self.backup_path(generation))?;
                }
            }
        }
        state.written = 0;
        Ok(())
    }

    /// `<path>.<generation>`.
    fn backup_path(&self, generation: u32) -> PathBuf {
        let mut name = self.path.as_os_str().to_owned();
        name.push(format!(".{generation}"));
        PathBuf::from(name)
    }
}

fn remove_if_present(path: &Path) -> std::io::Result<()> {
    match std::fs::remove_file(path) {
        Err(error) if error.kind() != std::io::ErrorKind::NotFound => Err(error),
        _ => Ok(()),
    }
}
