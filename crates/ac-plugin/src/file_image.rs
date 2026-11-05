//! Files a plugin points the client at by a path relative to its install
//! folder, such as the PNG and JPEG images its markup draws
//! (`<icon file="icons/sword.png"/>`). The plugin itself is sandboxed; the
//! host reads these files on its behalf, so one check serves them all and
//! none can be used to read outside the folder: the path must be relative,
//! stay inside the folder lexically and once every link along it is
//! followed, and name a file that really is what it claims to be. Nothing
//! here panics or throws: every refusal is a reason, and the caller draws
//! nothing in its place.

use std::collections::{HashMap, VecDeque};
use std::fs::File;
use std::path::{Component, Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, PoisonError};

use ac_plugin_ui::icon::{ICON_EXTENT, ICON_MAXIMUM_BYTES, PluginIcon, decode_png, validate_icon};
use ac_plugin_ui::{DecodedImage, FileImage, FileImageLoader, FileImageRead, FileImageResult};

/// The largest image file, bytes (1 MiB).
pub const MAXIMUM_BYTES: usize = 1024 * 1024;

/// The widest or tallest an image file may be, pixels.
pub const MAXIMUM_DIMENSION: u32 = 512;

/// The names an image file may end in (letter case ignored).
const IMAGE_EXTENSIONS: [&str; 3] = [".png", ".jpg", ".jpeg"];

const PNG_SIGNATURE: [u8; 8] = [137, 80, 78, 71, 13, 10, 26, 10];
const JPEG_SIGNATURE: [u8; 3] = [0xFF, 0xD8, 0xFF];

/// Why a path is refused when it leaves the folder.
const OUTSIDE: &str = "points outside the plugin folder";

/// Why a path that stays in the folder is refused when nothing is there.
const MISSING: &str = "does not exist";

/// The reader's refusal of a folder (and, on Windows, of anything that is
/// not a file on disk).
pub const NOT_A_REGULAR_FILE: &str = "is not a regular file";

const UNREADABLE_HEADER: &str = "has a header that does not say the image's size";

fn oversized() -> String {
    format!("is wider or taller than {MAXIMUM_DIMENSION} pixels")
}

/// The one spelling of a path a plugin names, so two spellings of the same
/// file (`./icons/a.png`, `icons//a.png`, `icons\a.png`) are one image:
/// separators become `/`, empty and `.` parts go, and each `..` folds into
/// the part before it, as [`resolve`] reads the path. A rooted path, or one
/// whose `..` climbs above the folder, comes back with only its separators
/// changed, for [`resolve`] to refuse. Letter case is kept as written.
#[must_use]
pub fn normalize(relative_path: &str) -> String {
    let path = relative_path.replace('\\', "/");
    if path.starts_with('/') || path.chars().nth(1) == Some(':') {
        return path;
    }
    let mut parts: Vec<&str> = Vec::new();
    for part in path.split('/') {
        match part {
            "" | "." => {}
            ".." => {
                if parts.pop().is_none() {
                    return path;
                }
            }
            part => parts.push(part),
        }
    }
    parts.join("/")
}

/// Resolves a path a plugin names against its install folder. The path
/// must be relative, end in one of `extensions` (letter case ignored), and
/// stay inside the folder lexically and after every link is followed. The
/// file's real place, with every link followed.
///
/// # Errors
/// The reason: "points outside the plugin folder", "is not `kind`", "does
/// not exist", or "could not be resolved: ...".
pub fn resolve(
    directory: &Path,
    relative_path: &str,
    extensions: &[&str],
    kind: &str,
) -> Result<PathBuf, String> {
    let relative = relative_path.replace('\\', "/");
    // A rooted path (`/x`, and so `\\server\share` and `\\?\` device paths
    // once their separators turn), a drive (`C:x`, `C:/x`), or anything the
    // system reads as absolute.
    if relative.is_empty()
        || relative.starts_with('/')
        || relative.chars().nth(1) == Some(':')
        || Path::new(&relative).is_absolute()
        || Path::new(&relative).has_root()
    {
        return Err(OUTSIDE.to_owned());
    }
    let lower = relative.to_ascii_lowercase();
    if !extensions.iter().any(|e| lower.ends_with(e)) {
        return Err(format!("is not {kind}"));
    }
    let resolution = |e: std::io::Error| format!("could not be resolved: {e}");
    let root = std::path::absolute(directory).map_err(resolution)?;
    // Lexically first, as the system would read the full path: every `..`
    // folds into the part before it, the folder's own parts included, and
    // what that comes to must lie under the folder.
    let Some(candidate) = lexical_candidate(&root, &relative) else {
        return Err(OUTSIDE.to_owned());
    };
    match std::fs::metadata(&candidate) {
        Ok(m) if !m.is_dir() => {}
        _ => return Err(MISSING.to_owned()),
    }
    // Then the real places: every link along both followed.
    let real_root = std::fs::canonicalize(&root).map_err(resolution)?;
    let real = std::fs::canonicalize(&candidate).map_err(resolution)?;
    if real == real_root || !real.starts_with(&real_root) {
        return Err(OUTSIDE.to_owned());
    }
    Ok(real)
}

/// `relative` (separators already `/`) joined to `root` with every `.` and
/// `..` folded in, the way the system reads the full path; none when that
/// does not lie under `root`. A part the system reads as a root or a drive
/// replaces the whole path when joined, so it never lies under it either.
fn lexical_candidate(root: &Path, relative: &str) -> Option<PathBuf> {
    let mut candidate = root.to_path_buf();
    for part in relative.split('/') {
        match part {
            "" | "." => {}
            ".." => {
                if !candidate.pop() {
                    return None;
                }
            }
            part => candidate.push(part),
        }
    }
    let rest = candidate.strip_prefix(root).ok()?;
    let inside = rest.components().next().is_some()
        && rest.components().all(|c| matches!(c, Component::Normal(_)));
    inside.then_some(candidate)
}

/// [`resolve`] for an image file: relative, ending in `.png`, `.jpg` or
/// `.jpeg`, and inside the folder after links are followed.
///
/// # Errors
/// The reason, as [`resolve`] gives it.
pub fn resolve_image_path(directory: &Path, relative_path: &str) -> Result<PathBuf, String> {
    resolve(
        directory,
        relative_path,
        &IMAGE_EXTENSIONS,
        "a .png or .jpg file",
    )
}

/// Reads the dock icon a plugin window names, under the rules of a plugin's
/// `icon.png`: relative and inside the folder after links are followed, a
/// `.png` by name and by signature, exactly 64 x 64 pixels, at most 64 KiB and
/// not animated. None when there is no such file.
///
/// # Errors
/// Why a file that is there, or a path that leaves the folder, cannot be used.
pub fn load_dock_icon(directory: &Path, relative_path: &str) -> Result<Option<PluginIcon>, String> {
    let path = normalize(relative_path);
    let full_path = match resolve(directory, &path, &[".png"], "a .png file") {
        Ok(full_path) => full_path,
        Err(reason) if reason == MISSING => return Ok(None),
        Err(reason) => return Err(reason),
    };
    let bytes = read_regular_file(&full_path, ICON_MAXIMUM_BYTES + 1)?;
    // The log adds the full stop the rules' own messages end in.
    validate_icon(&bytes).map_err(|error| error.to_string().trim_end_matches('.').to_owned())?;
    decode_png(&bytes)
        .filter(|icon| icon.width == ICON_EXTENT && icon.height == ICON_EXTENT)
        .map(Some)
        .ok_or_else(|| "has pixels that do not decode".to_owned())
}

/// Opens `path` for reading without ever waiting on it: on Linux and macOS
/// the file is opened non-blocking, so a pipe (FIFO) nobody writes to
/// cannot hold the reader up, and never as the controlling terminal.
#[cfg(windows)]
fn open(path: &Path) -> std::io::Result<File> {
    File::open(path)
}

/// `O_NONBLOCK | O_NOCTTY` where their values are these (they differ on
/// some processor families); elsewhere none, and plugin files are not read.
#[cfg(all(
    any(target_os = "linux", target_os = "android"),
    any(
        target_arch = "x86",
        target_arch = "x86_64",
        target_arch = "arm",
        target_arch = "aarch64",
        target_arch = "riscv64"
    )
))]
const NON_BLOCKING_OPEN: Option<i32> = Some(0o4000 | 0o400);

/// `O_NONBLOCK | O_NOCTTY` on Apple systems.
#[cfg(all(
    any(target_os = "macos", target_os = "ios"),
    any(target_arch = "x86_64", target_arch = "aarch64")
))]
const NON_BLOCKING_OPEN: Option<i32> = Some(0x4 | 0x2_0000);

/// Elsewhere the values are not known here.
#[cfg(all(
    unix,
    not(any(
        all(
            any(target_os = "linux", target_os = "android"),
            any(
                target_arch = "x86",
                target_arch = "x86_64",
                target_arch = "arm",
                target_arch = "aarch64",
                target_arch = "riscv64"
            )
        ),
        all(
            any(target_os = "macos", target_os = "ios"),
            any(target_arch = "x86_64", target_arch = "aarch64")
        )
    ))
))]
const NON_BLOCKING_OPEN: Option<i32> = None;

#[cfg(unix)]
fn open(path: &Path) -> std::io::Result<File> {
    use std::os::unix::fs::OpenOptionsExt;
    let Some(flags) = NON_BLOCKING_OPEN else {
        return Err(unsupported());
    };
    std::fs::OpenOptions::new()
        .read(true)
        .custom_flags(flags)
        .open(path)
}

/// Elsewhere plugin files are not read.
#[cfg(not(any(windows, unix)))]
fn open(_path: &Path) -> std::io::Result<File> {
    Err(unsupported())
}

#[cfg(not(windows))]
fn unsupported() -> std::io::Error {
    std::io::Error::new(
        std::io::ErrorKind::Unsupported,
        "reading plugin files is not supported on this system",
    )
}

/// One read at `offset` through the handle: never the path looked up a
/// second time, and on a pipe (which has no offsets) an error, not a wait.
fn read_at(file: &File, buffer: &mut [u8], offset: u64) -> std::io::Result<usize> {
    #[cfg(unix)]
    {
        std::os::unix::fs::FileExt::read_at(file, buffer, offset)
    }
    #[cfg(windows)]
    {
        std::os::windows::fs::FileExt::seek_read(file, buffer, offset)
    }
    #[cfg(not(any(unix, windows)))]
    {
        let _ = (file, buffer, offset);
        Err(std::io::Error::from(std::io::ErrorKind::Unsupported))
    }
}

/// Reads at most `read_limit` bytes of `path` through one open handle,
/// never waiting on it. A folder is refused as not a regular file, and so,
/// on Windows, is anything that is not a file on disk (a device or pipe).
/// On Linux and macOS a pipe's read fails (it has no offsets) and a device
/// that never ends (such as `/dev/zero`) fills the limit, so the caller
/// refuses each for what it read. A caller that allows n bytes passes
/// n + 1, so a longer file shows as one byte too many instead of being
/// read whole.
///
/// # Errors
/// [`NOT_A_REGULAR_FILE`], or "could not be read: ...".
pub fn read_regular_file(path: &Path, read_limit: usize) -> Result<Vec<u8>, String> {
    let read_limit = read_limit.max(1);
    let unreadable = |e: std::io::Error| format!("could not be read: {e}");
    if path.is_dir() {
        return Err(NOT_A_REGULAR_FILE.to_owned());
    }
    let file = open(path).map_err(unreadable)?;
    let metadata = file.metadata().map_err(unreadable)?;
    if cfg!(windows) && !metadata.is_file() {
        return Err(NOT_A_REGULAR_FILE.to_owned());
    }
    let length = usize::try_from(metadata.len()).unwrap_or(usize::MAX);
    let mut buffer = vec![0u8; length.saturating_add(1).clamp(1, read_limit)];
    let mut total = 0usize;
    loop {
        if total == buffer.len() {
            if buffer.len() == read_limit {
                break;
            }
            // The file grew since its length was read: keep reading, up to the limit.
            let grown = buffer.len().saturating_mul(2).min(read_limit);
            buffer.resize(grown, 0);
        }
        let read = read_at(&file, &mut buffer[total..], total as u64).map_err(unreadable)?;
        if read == 0 {
            break;
        }
        total += read;
    }
    buffer.truncate(total);
    Ok(buffer)
}

/// Reads and decodes the image file at `full_path` (from
/// [`resolve_image_path`]) to 8-bit RGBA: any size up to
/// [`MAXIMUM_DIMENSION`] on a side, the file at most [`MAXIMUM_BYTES`], and
/// its own bytes a PNG or a JPEG whatever its name. The header is read on
/// its own first: a decode allocates the whole image, so an oversized one is
/// refused on its declared size, before that allocation, and one whose
/// header does not say its size is refused too.
///
/// # Errors
/// The reason: "is larger than 1 MiB", "is not a PNG or JPEG file", "has a
/// header that does not say the image's size", "is wider or taller than
/// 512 pixels", "could not be decoded: ...", "could not be read: ...".
pub fn load_image(full_path: &Path) -> Result<DecodedImage, String> {
    let bytes = read_regular_file(full_path, MAXIMUM_BYTES + 1)?;
    if bytes.len() > MAXIMUM_BYTES {
        return Err("is larger than 1 MiB".to_owned());
    }
    if !bytes.starts_with(&PNG_SIGNATURE) && !bytes.starts_with(&JPEG_SIGNATURE) {
        return Err("is not a PNG or JPEG file".to_owned());
    }
    let Some((width, height)) = crate::image_codec::dimensions(&bytes) else {
        return Err(UNREADABLE_HEADER.to_owned());
    };
    if width > MAXIMUM_DIMENSION || height > MAXIMUM_DIMENSION {
        return Err(oversized());
    }
    let decoded =
        crate::image_codec::decode(&bytes).map_err(|e| format!("could not be decoded: {e}"))?;
    if decoded.width == 0 || decoded.height == 0 || decoded.rgba.is_empty() {
        return Err("could not be decoded: it has no pixels".to_owned());
    }
    if decoded.width > MAXIMUM_DIMENSION || decoded.height > MAXIMUM_DIMENSION {
        return Err(oversized());
    }
    Ok(DecodedImage {
        width: decoded.width,
        height: decoded.height,
        rgba: decoded.rgba,
    })
}

#[cfg(test)]
mod tests;

/// Which version of the file at `full_path` is on disk now: its length and
/// the time it was last written, the same for the same content.
///
/// # Errors
/// The file cannot be looked at.
pub fn version_of(full_path: &Path) -> std::io::Result<String> {
    let metadata = std::fs::metadata(full_path)?;
    let written = metadata
        .modified()?
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |d| d.as_nanos());
    Ok(format!("{}:{written}", metadata.len()))
}

/// One read of an image file, as the loader runs it.
pub type FileImageJob = Box<dyn FnOnce() -> FileImageResult + Send>;

/// Runs a read and hands back where its result will land.
pub type FileImageRunner = Box<dyn Fn(FileImageJob) -> FileImageRead + Send + Sync>;

/// The most threads reading plugin image files at once.
pub const READ_THREADS: usize = 2;

/// Reads waiting for a worker, and the workers: at most `max` threads,
/// started as reads come and ended when none are left, so an idle client
/// keeps none.
struct ReadPool {
    max: usize,
    state: Mutex<PoolState>,
    /// Reads run, for tests.
    ran: AtomicUsize,
}

#[derive(Default)]
struct PoolState {
    queue: VecDeque<(FileImageJob, FileImageRead)>,
    workers: usize,
}

impl ReadPool {
    fn new(max: usize) -> Self {
        Self {
            max: max.max(1),
            state: Mutex::new(PoolState::default()),
            ran: AtomicUsize::new(0),
        }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, PoolState> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn submit(self: &Arc<Self>, job: FileImageJob, read: FileImageRead) {
        let start = {
            let mut state = self.lock();
            state.queue.push_back((job, read));
            let start = state.workers < self.max;
            if start {
                state.workers += 1;
            }
            start
        };
        if !start {
            return;
        }
        let pool = Arc::clone(self);
        let started = std::thread::Builder::new()
            .name("plugin file image".to_owned())
            .spawn(move || pool.work());
        if let Err(e) = started {
            let mut state = self.lock();
            state.workers -= 1;
            if state.workers == 0 {
                // No worker is left to take them: they come back refused.
                for (_, read) in state.queue.drain(..) {
                    read.finish(Err(format!("could not be read: {e}")));
                }
            }
        }
    }

    /// One worker: runs reads until none are left. A worker counts itself
    /// out under the same lock that finds the queue empty, so a read
    /// queued meanwhile always has a worker.
    fn work(&self) {
        loop {
            let next = {
                let mut state = self.lock();
                match state.queue.pop_front() {
                    Some(next) => next,
                    None => {
                        state.workers -= 1;
                        return;
                    }
                }
            };
            let (job, read) = next;
            self.ran.fetch_add(1, Ordering::Relaxed);
            // A read that panics (a bug) ends its worker: it counts itself
            // out, so the next read starts another.
            let running = Running(self);
            read.finish(job());
            std::mem::forget(running);
        }
    }
}

/// A worker running a read; dropped only if the read panics.
struct Running<'a>(&'a ReadPool);

impl Drop for Running<'_> {
    fn drop(&mut self) {
        self.0.lock().workers -= 1;
    }
}

/// Runs each read on `pool`.
fn pool_runner(pool: Arc<ReadPool>) -> FileImageRunner {
    Box::new(move |job| {
        let read = FileImageRead::pending();
        pool.submit(job, read.clone());
        read
    })
}
/// Reads the image files plugin markup names away from the session's
/// thread: resolving the path, reading the file, decoding it and building
/// its smaller levels all happen on a worker, so a large image does not
/// hold up the session that first draws it. Windows that ask for the same
/// file while it is being read share the one read. Nothing is kept once
/// read: the result is held, counted and given back through the plugin's
/// own image table.
pub struct PluginFileImageLoader {
    run: FileImageRunner,
    reading: Mutex<HashMap<(PathBuf, String), FileImageRead>>,
    /// The worker threads, on the loader [`Self::new`] makes.
    pool: Option<Arc<ReadPool>>,
}

impl std::fmt::Debug for PluginFileImageLoader {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PluginFileImageLoader")
            .finish_non_exhaustive()
    }
}

impl Default for PluginFileImageLoader {
    fn default() -> Self {
        Self::new()
    }
}

impl PluginFileImageLoader {
    /// A loader that reads on at most [`READ_THREADS`] worker threads, the
    /// rest of the reads waiting their turn.
    #[must_use]
    pub fn new() -> Self {
        let pool = Arc::new(ReadPool::new(READ_THREADS));
        Self {
            pool: Some(Arc::clone(&pool)),
            ..Self::with_runner(pool_runner(pool))
        }
    }

    /// A loader whose reads `run` runs (a test runs them where it likes).
    #[must_use]
    pub fn with_runner(run: FileImageRunner) -> Self {
        Self {
            run,
            reading: Mutex::new(HashMap::new()),
            pool: None,
        }
    }

    /// How many reads the loader's worker threads have run (none for a
    /// loader whose reads a test runs).
    #[must_use]
    pub fn reads_on_workers(&self) -> usize {
        self.pool
            .as_ref()
            .map_or(0, |p| p.ran.load(Ordering::Relaxed))
    }

    fn read(directory: &Path, path: &str) -> FileImageResult {
        let full_path = resolve_image_path(directory, path)?;
        // Read before the content, so a file written while it is read is at
        // worst read again by the next window, never kept as current.
        let version = version_of(&full_path).map_err(|e| format!("could not be read: {e}"))?;
        let image = load_image(&full_path)?;
        Ok(Arc::new(FileImage {
            path: path.to_owned(),
            version,
            levels: ac_plugin_ui::mips::build(image.rgba, image.width, image.height),
            full_path: Some(full_path),
        }))
    }
}

impl FileImageLoader for PluginFileImageLoader {
    fn load(&self, directory: &Path, path: &str) -> FileImageRead {
        // One spelling per file, so its read, and the image it makes, are
        // shared by every spelling.
        let path = normalize(path);
        let key = (directory.to_path_buf(), path.clone());
        let mut reading = self.reading.lock().unwrap_or_else(PoisonError::into_inner);
        // Only a read still under way is shared: one that has finished may
        // be of a version of the file that has since changed.
        reading.retain(|_, read| !read.is_finished());
        if let Some(read) = reading.get(&key) {
            return read.clone();
        }
        let directory = directory.to_path_buf();
        let read = (self.run)(Box::new(move || Self::read(&directory, &path)));
        if !read.is_finished() {
            reading.insert(key, read.clone());
        }
        read
    }

    fn has_file(&self, directory: &Path, path: &str) -> bool {
        resolve_image_path(directory, &normalize(path)).is_ok()
    }

    fn read_dock_icon(&self, directory: &Path, path: &str) -> Result<Option<PluginIcon>, String> {
        load_dock_icon(directory, path)
    }

    fn is_current(&self, image: &FileImage) -> bool {
        image
            .full_path
            .as_deref()
            .is_none_or(|full| version_of(full).is_ok_and(|v| v == image.version))
    }
}

#[cfg(test)]
mod loader_tests;
