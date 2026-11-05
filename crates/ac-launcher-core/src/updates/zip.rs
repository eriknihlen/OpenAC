//! Reading zip archives (stored and deflated entries, ZIP64) and extracting
//! release payloads safely: every path portable and contained, no links,
//! bounded counts, sizes and compression ratios, hashes recorded, an
//! all-or-nothing result.

use std::collections::{HashMap, HashSet};
use std::fs::{self, File};
use std::io::{self, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};

use miniz_oxide::inflate::stream::{InflateState, inflate};
use miniz_oxide::{DataFormat, MZError, MZFlush, MZStatus};

use super::UpdateError;
use super::payload::is_windows_device_name;
use crate::cancel::CancelToken;
use crate::fsutil;
use crate::integrity::Sha256Hasher;

// ---------------------------------------------------------------------------
// The archive format.

fn invalid(message: impl Into<String>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message.into())
}

fn u16_at(bytes: &[u8], offset: usize) -> io::Result<u16> {
    bytes
        .get(offset..offset + 2)
        .map(|slice| u16::from_le_bytes([slice[0], slice[1]]))
        .ok_or_else(|| invalid("The zip archive is truncated."))
}

fn u32_at(bytes: &[u8], offset: usize) -> io::Result<u32> {
    bytes
        .get(offset..offset + 4)
        .map(|slice| u32::from_le_bytes([slice[0], slice[1], slice[2], slice[3]]))
        .ok_or_else(|| invalid("The zip archive is truncated."))
}

fn u64_at(bytes: &[u8], offset: usize) -> io::Result<u64> {
    bytes
        .get(offset..offset + 8)
        .map(|slice| {
            let mut value = [0u8; 8];
            value.copy_from_slice(slice);
            u64::from_le_bytes(value)
        })
        .ok_or_else(|| invalid("The zip archive is truncated."))
}

/// One entry's central-directory record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ZipEntry {
    /// The name as stored (UTF-8).
    pub name: String,
    /// 0 (stored) or 8 (deflated); others are refused when read.
    pub method: u16,
    /// General-purpose flags.
    pub flags: u16,
    /// CRC-32 of the uncompressed bytes.
    pub crc32: u32,
    /// Compressed size.
    pub compressed_size: u64,
    /// Uncompressed size.
    pub uncompressed_size: u64,
    /// External attributes (Unix mode in the high 16 bits, DOS attributes
    /// in the low ones).
    pub external_attributes: u32,
    local_header_offset: u64,
}

/// An open zip archive.
#[derive(Debug)]
pub struct ZipArchive {
    file: File,
    length: u64,
    entries: Vec<ZipEntry>,
}

const EOCD: u32 = 0x0605_4b50;
const ZIP64_LOCATOR: u32 = 0x0706_4b50;
const ZIP64_EOCD: u32 = 0x0606_4b50;
const CENTRAL: u32 = 0x0201_4b50;
const LOCAL: u32 = 0x0403_4b50;

impl ZipArchive {
    /// Opens an archive and reads its central directory.
    pub fn open(path: &Path) -> io::Result<Self> {
        let mut file = File::open(path)?;
        let length = file.metadata()?.len();
        let tail_length = length.min(22 + 65_535);
        let mut tail = vec![0u8; tail_length as usize];
        file.seek(SeekFrom::Start(length - tail_length))?;
        file.read_exact(&mut tail)?;
        let eocd = (0..tail.len().saturating_sub(21))
            .rev()
            .find(|&index| u32_at(&tail, index).is_ok_and(|value| value == EOCD))
            .ok_or_else(|| invalid("The file is not a zip archive."))?;
        let mut count = u64::from(u16_at(&tail, eocd + 10)?);
        let mut directory_size = u64::from(u32_at(&tail, eocd + 12)?);
        let mut directory_offset = u64::from(u32_at(&tail, eocd + 16)?);
        if count == 0xFFFF || directory_size == 0xFFFF_FFFF || directory_offset == 0xFFFF_FFFF {
            let locator_at = eocd
                .checked_sub(20)
                .ok_or_else(|| invalid("The ZIP64 locator is missing."))?;
            if u32_at(&tail, locator_at)? != ZIP64_LOCATOR {
                return Err(invalid("The ZIP64 locator is missing."));
            }
            let zip64_offset = u64_at(&tail, locator_at + 8)?;
            let mut record = [0u8; 56];
            file.seek(SeekFrom::Start(zip64_offset))?;
            file.read_exact(&mut record)?;
            if u32_at(&record, 0)? != ZIP64_EOCD {
                return Err(invalid("The ZIP64 end record is missing."));
            }
            count = u64_at(&record, 32)?;
            directory_size = u64_at(&record, 40)?;
            directory_offset = u64_at(&record, 48)?;
        }
        if directory_offset
            .checked_add(directory_size)
            .is_none_or(|end| end > length)
            || directory_size > 512 * 1024 * 1024
        {
            return Err(invalid("The zip central directory is out of range."));
        }
        let mut directory = vec![0u8; directory_size as usize];
        file.seek(SeekFrom::Start(directory_offset))?;
        file.read_exact(&mut directory)?;
        let mut entries = Vec::new();
        let mut at = 0usize;
        for _ in 0..count {
            if u32_at(&directory, at)? != CENTRAL {
                return Err(invalid("A zip central directory record is damaged."));
            }
            let flags = u16_at(&directory, at + 8)?;
            let method = u16_at(&directory, at + 10)?;
            let crc32 = u32_at(&directory, at + 16)?;
            let mut compressed_size = u64::from(u32_at(&directory, at + 20)?);
            let mut uncompressed_size = u64::from(u32_at(&directory, at + 24)?);
            let name_length = usize::from(u16_at(&directory, at + 28)?);
            let extra_length = usize::from(u16_at(&directory, at + 30)?);
            let comment_length = usize::from(u16_at(&directory, at + 32)?);
            let external_attributes = u32_at(&directory, at + 38)?;
            let mut local_header_offset = u64::from(u32_at(&directory, at + 42)?);
            let name_start = at + 46;
            let name_bytes = directory
                .get(name_start..name_start + name_length)
                .ok_or_else(|| invalid("The zip archive is truncated."))?;
            let name = String::from_utf8(name_bytes.to_vec())
                .map_err(|_| invalid("A zip entry name is not UTF-8."))?;
            let extra_start = name_start + name_length;
            let extra = directory
                .get(extra_start..extra_start + extra_length)
                .ok_or_else(|| invalid("The zip archive is truncated."))?;
            let mut extra_at = 0;
            while extra_at + 4 <= extra.len() {
                let id = u16_at(extra, extra_at)?;
                let size = usize::from(u16_at(extra, extra_at + 2)?);
                let data = extra
                    .get(extra_at + 4..extra_at + 4 + size)
                    .ok_or_else(|| invalid("A zip extra field is damaged."))?;
                if id == 0x0001 {
                    let mut field = 0;
                    if uncompressed_size == 0xFFFF_FFFF {
                        uncompressed_size = u64_at(data, field)?;
                        field += 8;
                    }
                    if compressed_size == 0xFFFF_FFFF {
                        compressed_size = u64_at(data, field)?;
                        field += 8;
                    }
                    if local_header_offset == 0xFFFF_FFFF {
                        local_header_offset = u64_at(data, field)?;
                    }
                }
                extra_at += 4 + size;
            }
            entries.push(ZipEntry {
                name,
                method,
                flags,
                crc32,
                compressed_size,
                uncompressed_size,
                external_attributes,
                local_header_offset,
            });
            at = extra_start + extra_length + comment_length;
        }
        Ok(Self {
            file,
            length,
            entries,
        })
    }

    /// The entries in central-directory order.
    pub fn entries(&self) -> &[ZipEntry] {
        &self.entries
    }

    /// A reader over one entry's uncompressed bytes; the CRC-32 and the size
    /// are checked when it reaches the end.
    pub fn reader(&mut self, index: usize) -> io::Result<EntryReader<'_>> {
        let entry = self
            .entries
            .get(index)
            .cloned()
            .ok_or_else(|| invalid("No such zip entry."))?;
        if entry.flags & 1 != 0 {
            return Err(invalid(format!("Zip entry '{}' is encrypted.", entry.name)));
        }
        if entry.method != 0 && entry.method != 8 {
            return Err(invalid(format!(
                "Zip entry '{}' uses an unsupported compression method {}.",
                entry.name, entry.method
            )));
        }
        let mut header = [0u8; 30];
        self.file.seek(SeekFrom::Start(entry.local_header_offset))?;
        self.file.read_exact(&mut header)?;
        if u32_at(&header, 0)? != LOCAL {
            return Err(invalid("A zip local header is damaged."));
        }
        let skip = u64::from(u16_at(&header, 26)?) + u64::from(u16_at(&header, 28)?);
        let data_start = entry.local_header_offset + 30 + skip;
        if data_start
            .checked_add(entry.compressed_size)
            .is_none_or(|end| end > self.length)
        {
            return Err(invalid("A zip entry runs past the end of the archive."));
        }
        self.file.seek(SeekFrom::Start(data_start))?;
        let inflater = (entry.method == 8).then(|| InflateState::new_boxed(DataFormat::Raw));
        Ok(EntryReader {
            source: (&mut self.file).take(entry.compressed_size),
            inflater,
            input: vec![0u8; 64 * 1024],
            input_start: 0,
            input_end: 0,
            source_done: false,
            stream_done: false,
            crc: Crc32::new(),
            produced: 0,
            entry,
        })
    }
}

/// Streams one entry's uncompressed bytes.
pub struct EntryReader<'a> {
    source: io::Take<&'a mut File>,
    inflater: Option<Box<InflateState>>,
    input: Vec<u8>,
    input_start: usize,
    input_end: usize,
    source_done: bool,
    stream_done: bool,
    crc: Crc32,
    produced: u64,
    entry: ZipEntry,
}

impl std::fmt::Debug for EntryReader<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("EntryReader")
            .field("entry", &self.entry.name)
            .finish_non_exhaustive()
    }
}

impl EntryReader<'_> {
    fn finish(&mut self) -> io::Result<()> {
        if self.produced != self.entry.uncompressed_size {
            return Err(invalid(format!(
                "Zip entry '{}' has the wrong length.",
                self.entry.name
            )));
        }
        if self.crc.value() != self.entry.crc32 {
            return Err(invalid(format!(
                "Zip entry '{}' fails its CRC check.",
                self.entry.name
            )));
        }
        Ok(())
    }

    fn fill(&mut self) -> io::Result<()> {
        if self.input_start == self.input_end && !self.source_done {
            let read = self.source.read(&mut self.input)?;
            self.input_start = 0;
            self.input_end = read;
            if read == 0 {
                self.source_done = true;
            }
        }
        Ok(())
    }
}

impl Read for EntryReader<'_> {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        if buffer.is_empty() || self.stream_done {
            return Ok(0);
        }
        loop {
            self.fill()?;
            let produced = match &mut self.inflater {
                None => {
                    let count = (self.input_end - self.input_start).min(buffer.len());
                    buffer[..count]
                        .copy_from_slice(&self.input[self.input_start..self.input_start + count]);
                    self.input_start += count;
                    if count == 0 && self.source_done {
                        self.stream_done = true;
                    }
                    count
                }
                Some(state) => {
                    let result = inflate(
                        state,
                        &self.input[self.input_start..self.input_end],
                        buffer,
                        MZFlush::None,
                    );
                    self.input_start += result.bytes_consumed;
                    let progress = result.bytes_written > 0 || result.bytes_consumed > 0;
                    match result.status {
                        Ok(MZStatus::StreamEnd) => self.stream_done = true,
                        Ok(_) => {}
                        // No progress: more input is needed.
                        Err(MZError::Buf) => {}
                        Err(_) => {
                            return Err(invalid(format!(
                                "Zip entry '{}' has damaged compressed data.",
                                self.entry.name
                            )));
                        }
                    }
                    if !progress && !self.stream_done && self.source_done {
                        return Err(invalid(format!(
                            "Zip entry '{}' ends early.",
                            self.entry.name
                        )));
                    }
                    result.bytes_written
                }
            };
            if produced > 0 {
                self.crc.update(&buffer[..produced]);
                self.produced += produced as u64;
                if self.produced > self.entry.uncompressed_size {
                    return Err(invalid(format!(
                        "Zip entry '{}' is longer than declared.",
                        self.entry.name
                    )));
                }
                return Ok(produced);
            }
            if self.stream_done {
                self.finish()?;
                return Ok(0);
            }
        }
    }
}

const CRC_TABLE: [u32; 256] = {
    let mut table = [0u32; 256];
    let mut index = 0;
    while index < 256 {
        let mut crc = index as u32;
        let mut bit = 0;
        while bit < 8 {
            crc = if crc & 1 != 0 {
                (crc >> 1) ^ 0xEDB8_8320
            } else {
                crc >> 1
            };
            bit += 1;
        }
        table[index] = crc;
        index += 1;
    }
    table
};

/// CRC-32 (IEEE), as zip uses.
#[derive(Debug, Clone, Copy)]
struct Crc32(u32);

impl Crc32 {
    const fn new() -> Self {
        Self(0xFFFF_FFFF)
    }

    fn update(&mut self, bytes: &[u8]) {
        let mut crc = self.0;
        for &byte in bytes {
            crc = CRC_TABLE[((crc ^ u32::from(byte)) & 0xFF) as usize] ^ (crc >> 8);
        }
        self.0 = crc;
    }

    const fn value(self) -> u32 {
        !self.0
    }
}

/// The CRC-32 of `bytes` (for writing fixtures).
pub fn crc32(bytes: &[u8]) -> u32 {
    let mut crc = Crc32::new();
    crc.update(bytes);
    crc.value()
}

// ---------------------------------------------------------------------------
// Safe extraction.

/// Caps on what one archive may unpack.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SafeZipExtractionLimits {
    /// Entries in the archive.
    pub maximum_entries: usize,
    /// Uncompressed bytes in one entry.
    pub maximum_entry_bytes: u64,
    /// Uncompressed bytes in all entries.
    pub maximum_total_bytes: u64,
    /// Uncompressed over compressed size, per entry and overall.
    pub maximum_compression_ratio: f64,
    /// Characters in one entry's relative path.
    pub maximum_relative_path_length: usize,
}

impl Default for SafeZipExtractionLimits {
    fn default() -> Self {
        Self {
            maximum_entries: 20_000,
            maximum_entry_bytes: 2 * 1024 * 1024 * 1024,
            maximum_total_bytes: 8 * 1024 * 1024 * 1024,
            maximum_compression_ratio: 200.0,
            maximum_relative_path_length: 512,
        }
    }
}

/// One extracted file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ExtractedFileRecord {
    /// Path relative to the extraction root, `/`-separated.
    pub path: String,
    /// SHA-256 of the file, lowercase hex.
    pub sha256: String,
    /// Size in bytes.
    pub size: u64,
    /// The permission bits it was given (0o755 or 0o644 unless declared).
    pub unix_mode: u32,
}

impl ExtractedFileRecord {
    /// A record.
    pub fn new(
        path: impl Into<String>,
        sha256: impl Into<String>,
        size: u64,
        unix_mode: u32,
    ) -> Self {
        Self {
            path: path.into(),
            sha256: sha256.into(),
            size,
            unix_mode,
        }
    }
}

const UNIX_TYPE_MASK: u32 = 0xF000;
const UNIX_REGULAR_FILE: u32 = 0x8000;
const UNIX_DIRECTORY: u32 = 0x4000;
const UNIX_PERMISSION_MASK: u32 = 0x1FF;
const EXECUTABLE_MODE: u32 = 0o755;
const REGULAR_FILE_MODE: u32 = 0o644;
const DOS_REPARSE_POINT: u32 = 0x400;

type ModeSetter = Box<dyn Fn(&Path, u32) -> io::Result<()> + Send + Sync>;

/// Extracts release and plugin archives under strict rules.
pub struct SafeZipExtractor {
    limits: SafeZipExtractionLimits,
    apply_unix_file_mode: Option<ModeSetter>,
    ignore_declared_modes: bool,
}

impl std::fmt::Debug for SafeZipExtractor {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SafeZipExtractor")
            .field("limits", &self.limits)
            .field("ignore_declared_modes", &self.ignore_declared_modes)
            .finish_non_exhaustive()
    }
}

impl Default for SafeZipExtractor {
    fn default() -> Self {
        Self::new(SafeZipExtractionLimits::default(), false)
    }
}

struct ValidatedEntry {
    index: usize,
    relative_path: String,
    is_directory: bool,
    unix_mode: u32,
    length: u64,
}

fn segment_is_unsafe(segment: &str) -> bool {
    segment.is_empty()
        || segment == "."
        || segment == ".."
        || segment.ends_with(' ')
        || segment.ends_with('.')
        || segment
            .chars()
            .any(|c| c.is_control() || matches!(c, '<' | '>' | '"' | '|' | '?' | '*'))
        || is_windows_device_name(segment)
}

fn parent_paths(relative: &str) -> Vec<String> {
    let path = relative.trim_end_matches('/');
    path.match_indices('/')
        .map(|(index, _)| path[..index].to_owned())
        .collect()
}

/// `root` joined with a validated relative path, refused when it would
/// leave `root`.
fn resolve_contained(root: &Path, relative: &str) -> Result<PathBuf, UpdateError> {
    let mut path = root.to_path_buf();
    for segment in relative.trim_end_matches('/').split('/') {
        path.push(segment);
    }
    let path = fsutil::full_path(&path);
    let root_text = root.to_string_lossy().into_owned();
    let prefix = if root_text.ends_with(std::path::MAIN_SEPARATOR) {
        root_text
    } else {
        format!("{root_text}{}", std::path::MAIN_SEPARATOR)
    };
    let text = path.to_string_lossy();
    let contained = if cfg!(windows) {
        text.to_lowercase().starts_with(&prefix.to_lowercase())
    } else {
        text.starts_with(&prefix)
    };
    if contained {
        Ok(path)
    } else {
        Err(UpdateError::failed(format!(
            "ZIP path '{relative}' escaped the extraction directory."
        )))
    }
}

fn reject_link(path: &Path, description: &str) -> Result<(), UpdateError> {
    match fsutil::is_link(path) {
        Ok(false) => Ok(()),
        Ok(true) => Err(UpdateError::failed(format!(
            "The {description} is a reparse point."
        ))),
        Err(error) => Err(UpdateError::io(
            format!("The release ZIP could not be extracted safely: {error}"),
            error,
        )),
    }
}

fn io_failure(error: io::Error) -> UpdateError {
    UpdateError::io(
        format!("The release ZIP could not be extracted safely: {error}"),
        error,
    )
}

impl SafeZipExtractor {
    /// An extractor with these limits. `ignore_declared_modes` writes every
    /// file as 0644 regardless of the archive (plugins). Modes are applied
    /// to the real file system on Unix.
    pub fn new(limits: SafeZipExtractionLimits, ignore_declared_modes: bool) -> Self {
        let apply: Option<ModeSetter> = if cfg!(unix) {
            Some(Box::new(fsutil::set_unix_mode))
        } else {
            None
        };
        Self {
            limits,
            apply_unix_file_mode: apply,
            ignore_declared_modes,
        }
    }

    /// Replaces how modes are applied (tests observe them on any platform).
    #[must_use]
    pub fn with_mode_setter(
        mut self,
        setter: impl Fn(&Path, u32) -> io::Result<()> + Send + Sync + 'static,
    ) -> Self {
        self.apply_unix_file_mode = Some(Box::new(setter));
        self
    }

    /// The limits in force.
    pub fn limits(&self) -> SafeZipExtractionLimits {
        self.limits
    }

    /// Extracts `archive_path` into `destination`, which must not exist or
    /// be empty. `executable_names` are root-relative paths given mode 0755.
    /// On any failure the destination is removed and nothing is published.
    /// Returns the files sorted by path.
    pub fn extract(
        &self,
        archive_path: &Path,
        destination: &Path,
        executable_names: &[String],
        cancel: &CancelToken,
    ) -> Result<Vec<ExtractedFileRecord>, UpdateError> {
        let limits = self.limits;
        if limits.maximum_entries == 0
            || limits.maximum_entry_bytes == 0
            || limits.maximum_total_bytes == 0
            || limits.maximum_compression_ratio <= 0.0
            || limits.maximum_relative_path_length == 0
        {
            return Err(UpdateError::failed(
                "ZIP extraction limits must all be positive.",
            ));
        }
        if self.ignore_declared_modes && !executable_names.is_empty() {
            return Err(UpdateError::failed(
                "executableNames cannot be used together with ignoreDeclaredModes.",
            ));
        }
        let archive = fsutil::full_path(archive_path);
        let destination = fsutil::full_path(destination);
        if destination.is_dir() && !fsutil::is_empty_directory(&destination) {
            return Err(UpdateError::failed(
                "The ZIP extraction destination must be empty.",
            ));
        }
        let result = self.extract_core(&archive, &destination, executable_names, cancel);
        if result.is_err() {
            fsutil::try_delete_tree(&destination);
        }
        result
    }

    fn extract_core(
        &self,
        archive: &Path,
        destination: &Path,
        executable_names: &[String],
        cancel: &CancelToken,
    ) -> Result<Vec<ExtractedFileRecord>, UpdateError> {
        let executables: HashSet<&str> = executable_names.iter().map(String::as_str).collect();
        let mut zip = ZipArchive::open(archive).map_err(io_failure)?;
        let entries = self.validate_archive(&zip)?;

        fs::create_dir_all(destination).map_err(io_failure)?;
        reject_link(destination, "extraction root")?;
        let mut directories: Vec<String> = Vec::new();
        let mut seen = HashSet::new();
        for directory in entries
            .iter()
            .flat_map(|entry| parent_paths(&entry.relative_path))
            .chain(
                entries
                    .iter()
                    .filter(|entry| entry.is_directory)
                    .map(|entry| entry.relative_path.clone()),
            )
        {
            if seen.insert(directory.to_lowercase()) {
                directories.push(directory);
            }
        }
        directories.sort_by(|left, right| {
            left.matches('/')
                .count()
                .cmp(&right.matches('/').count())
                .then_with(|| left.cmp(right))
        });
        for directory in &directories {
            cancel.check()?;
            let path = resolve_contained(destination, directory)?;
            fs::create_dir_all(&path).map_err(io_failure)?;
            reject_link(&path, &format!("directory '{directory}'"))?;
        }

        let mut files = Vec::new();
        let mut actual_total: u64 = 0;
        let mut buffer = vec![0u8; 128 * 1024];
        for entry in entries.iter().filter(|entry| !entry.is_directory) {
            cancel.check()?;
            let output_path = resolve_contained(destination, &entry.relative_path)?;
            for parent in parent_paths(&entry.relative_path) {
                let path = resolve_contained(destination, &parent)?;
                if !path.is_dir() {
                    return Err(UpdateError::failed(format!(
                        "ZIP parent '{parent}' is not a directory."
                    )));
                }
                reject_link(&path, &format!("directory '{parent}'"))?;
            }
            let mut input = zip.reader(entry.index).map_err(io_failure)?;
            let mut output = fsutil::create_new(&output_path, false).map_err(io_failure)?;
            let mut hash = Sha256Hasher::new();
            let mut actual_entry: u64 = 0;
            loop {
                cancel.check()?;
                let read = input.read(&mut buffer).map_err(io_failure)?;
                if read == 0 {
                    break;
                }
                actual_entry += read as u64;
                actual_total += read as u64;
                if actual_entry > entry.length
                    || actual_entry > self.limits.maximum_entry_bytes
                    || actual_total > self.limits.maximum_total_bytes
                {
                    return Err(UpdateError::failed(format!(
                        "ZIP entry '{}' exceeded its declared limits.",
                        entry.relative_path
                    )));
                }
                hash.update(&buffer[..read]);
                output.write_all(&buffer[..read]).map_err(io_failure)?;
            }
            output.flush().map_err(io_failure)?;
            output.sync_all().map_err(io_failure)?;
            drop(output);
            if actual_entry != entry.length {
                return Err(UpdateError::failed(format!(
                    "ZIP entry '{}' length changed while extracting.",
                    entry.relative_path
                )));
            }
            let declared = entry.unix_mode & UNIX_PERMISSION_MASK;
            let unix_mode = if self.ignore_declared_modes {
                REGULAR_FILE_MODE
            } else if executables.contains(entry.relative_path.as_str()) {
                EXECUTABLE_MODE
            } else if declared != 0 {
                declared
            } else {
                REGULAR_FILE_MODE
            };
            if let Some(apply) = &self.apply_unix_file_mode {
                apply(&output_path, unix_mode).map_err(io_failure)?;
            }
            files.push(ExtractedFileRecord {
                path: entry.relative_path.clone(),
                sha256: hash.finish_hex(),
                size: actual_entry,
                unix_mode,
            });
        }
        files.sort_by(|left, right| left.path.cmp(&right.path));
        Ok(files)
    }

    fn validate_archive(&self, zip: &ZipArchive) -> Result<Vec<ValidatedEntry>, UpdateError> {
        let count = zip.entries().len();
        if count == 0 || count > self.limits.maximum_entries {
            return Err(UpdateError::failed(format!(
                "ZIP entry count {count} is outside the allowed range."
            )));
        }
        let mut result = Vec::with_capacity(count);
        let mut explicit = HashSet::new();
        let mut nodes: HashMap<String, (String, bool)> = HashMap::new();
        let mut total_length: u64 = 0;
        let mut total_compressed: u64 = 0;
        for (index, entry) in zip.entries().iter().enumerate() {
            let relative = self.normalize_entry_path(&entry.name)?;
            if !explicit.insert(relative.to_lowercase()) {
                return Err(UpdateError::failed(format!(
                    "ZIP contains a duplicate/case-colliding entry '{relative}'."
                )));
            }
            let unix_attributes = entry.external_attributes >> 16;
            let unix_type = unix_attributes & UNIX_TYPE_MASK;
            let trailing_directory = entry.name.ends_with('/') || entry.name.ends_with('\\');
            let is_directory = trailing_directory || unix_type == UNIX_DIRECTORY;
            if entry.external_attributes & DOS_REPARSE_POINT != 0
                || !matches!(unix_type, 0 | UNIX_REGULAR_FILE | UNIX_DIRECTORY)
                || (unix_type == UNIX_DIRECTORY && !trailing_directory)
                || (is_directory && (entry.uncompressed_size != 0 || entry.compressed_size != 0))
            {
                return Err(UpdateError::failed(format!(
                    "ZIP entry '{relative}' is a symlink, reparse point, or unsupported type."
                )));
            }
            add_path_nodes(&mut nodes, &relative, is_directory)?;
            if !is_directory {
                if entry.uncompressed_size > self.limits.maximum_entry_bytes {
                    return Err(UpdateError::failed(format!(
                        "ZIP entry '{relative}' exceeds the per-file limit."
                    )));
                }
                total_length += entry.uncompressed_size;
                total_compressed += entry.compressed_size;
                if total_length > self.limits.maximum_total_bytes
                    || self.ratio_exceeded(entry.uncompressed_size, entry.compressed_size)
                {
                    return Err(UpdateError::failed(format!(
                        "ZIP entry '{relative}' exceeds extraction size/ratio limits."
                    )));
                }
            }
            result.push(ValidatedEntry {
                index,
                relative_path: relative,
                is_directory,
                unix_mode: unix_attributes,
                length: entry.uncompressed_size,
            });
        }
        if total_length > 0
            && (total_compressed == 0 || self.ratio_exceeded(total_length, total_compressed))
        {
            return Err(UpdateError::failed(
                "ZIP aggregate compression ratio exceeds the allowed limit.",
            ));
        }
        Ok(result)
    }

    fn ratio_exceeded(&self, expanded: u64, compressed: u64) -> bool {
        expanded > 0
            && (compressed == 0
                || expanded as f64 / compressed as f64 > self.limits.maximum_compression_ratio)
    }

    fn normalize_entry_path(&self, name: &str) -> Result<String, UpdateError> {
        if name.is_empty() || name.contains('\0') || name.contains(':') {
            return Err(UpdateError::failed(
                "ZIP contains an empty, NUL, or ADS path.",
            ));
        }
        let normalized = name.replace('\\', "/");
        let directory = normalized.ends_with('/');
        let normalized = normalized.trim_end_matches('/');
        if normalized.is_empty()
            || normalized.chars().count() > self.limits.maximum_relative_path_length
            || normalized.starts_with('/')
        {
            return Err(UpdateError::failed(format!(
                "ZIP path '{name}' is rooted or too long."
            )));
        }
        if normalized.split('/').any(segment_is_unsafe) {
            return Err(UpdateError::failed(format!(
                "ZIP path '{name}' contains an unsafe segment."
            )));
        }
        Ok(format!("{normalized}{}", if directory { "/" } else { "" }))
    }
}

fn add_path_nodes(
    nodes: &mut HashMap<String, (String, bool)>,
    relative: &str,
    is_directory: bool,
) -> Result<(), UpdateError> {
    let path = relative.trim_end_matches('/');
    let segments: Vec<&str> = path.split('/').collect();
    let mut current = String::new();
    for (index, segment) in segments.iter().enumerate() {
        if !current.is_empty() {
            current.push('/');
        }
        current.push_str(segment);
        let node_is_directory = index < segments.len() - 1 || is_directory;
        let key = current.to_lowercase();
        if let Some((spelling, existing_is_directory)) = nodes.get(&key) {
            if *spelling != current || !*existing_is_directory || !node_is_directory {
                return Err(UpdateError::failed(format!(
                    "ZIP path '{relative}' collides with '{spelling}'."
                )));
            }
            continue;
        }
        nodes.insert(key, (current.clone(), node_is_directory));
    }
    Ok(())
}

/// Deletes an extraction or staging directory without following links,
/// ignoring failures (leftovers are reclaimed by a later recovery pass).
pub fn try_delete_directory(path: &Path) {
    fsutil::try_delete_tree(path);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crc32_matches_the_standard_check_value() {
        assert_eq!(crc32(b"123456789"), 0xCBF4_3926);
    }
}
