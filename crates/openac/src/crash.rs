//! A local crash report: when any thread of the process panics, one
//! bounded JSON file is written to the crash reports folder, saying where
//! it failed, the stack, the GPU and window, and where the player was.
//!
//! The panic's message is never written (it can hold anything: names,
//! paths, text from the server); only where it happened. Writing is best
//! effort: a report that cannot be written is said so on standard error and
//! never hides the panic itself.

use crate::stdio::errln;
use std::fmt::Write as _;
use std::panic::PanicHookInfo;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, SystemTime};

use ac_render::facts::{PresentationFacts, SharedFacts};

/// The largest report written.
pub const MAX_REPORT_BYTES: usize = 256 * 1024;
const MAX_STACK_FRAMES: usize = 48;
const MAX_STACK_CHARS: usize = 3072;
const TRUNCATED: &str = "[truncated]";

/// What failed and where, without the message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Failure {
    /// The thread's name.
    pub thread: String,
    /// `file:line:column` of the panic.
    pub location: Option<String>,
    /// Function names of the stack, innermost first.
    pub stack: String,
}

/// The crash handler's settings, shared with the hook.
#[derive(Debug, Clone)]
pub struct CrashReporter {
    directory: Arc<Mutex<Option<PathBuf>>>,
    facts: SharedFacts,
}

impl CrashReporter {
    /// Install the panic hook (once, at startup). The previous hook still
    /// prints the panic as usual.
    #[must_use]
    pub fn install(directory: Option<PathBuf>, facts: SharedFacts) -> Self {
        let reporter = Self {
            directory: Arc::new(Mutex::new(directory)),
            facts,
        };
        let hook_reporter = reporter.clone();
        let previous = std::panic::take_hook();
        std::panic::set_hook(Box::new(move |info| {
            // A lost GPU device met inside a poll is caught there and ends
            // only the window, which reports it; the process goes on.
            if ac_render::device_health::contained_device_loss(info.payload()) {
                return;
            }
            previous(info);
            hook_reporter.report(info);
        }));
        reporter
    }

    /// Write later reports here instead.
    pub fn set_directory(&self, directory: Option<PathBuf>) {
        *self
            .directory
            .lock()
            .unwrap_or_else(PoisonError::into_inner) = directory;
    }

    fn report(&self, info: &PanicHookInfo<'_>) {
        let directory = match self.directory.try_lock() {
            Ok(guard) => guard.clone(),
            Err(std::sync::TryLockError::Poisoned(p)) => p.into_inner().clone(),
            Err(std::sync::TryLockError::WouldBlock) => None,
        };
        let Some(directory) = directory else {
            errln!("Local crash report unavailable: no diagnostics directory.");
            return;
        };
        let failure = Failure {
            thread: std::thread::current()
                .name()
                .unwrap_or("unnamed")
                .to_owned(),
            location: info
                .location()
                .map(|l| format!("{}:{}:{}", l.file(), l.line(), l.column())),
            stack: function_stack(&std::backtrace::Backtrace::force_capture().to_string()),
        };
        let json = report_json(&failure, self.facts.get().as_ref(), SystemTime::now());
        match write_report(&directory, &json) {
            Ok(path) => errln!("Local crash report saved: {}", path.display()),
            Err(_) => errln!("Local crash report unavailable."),
        }
    }
}

/// The function names of a printed backtrace, innermost first: the
/// numbered lines, without the source paths (`at ...` lines), at most 48
/// frames and 3072 characters.
#[must_use]
pub fn function_stack(backtrace: &str) -> String {
    let mut frames = backtrace.lines().filter_map(|line| {
        let line = line.trim_start();
        let (number, name) = line.split_once(": ")?;
        number
            .chars()
            .all(|c| c.is_ascii_digit())
            .then(|| name.trim())
    });
    let mut out = String::new();
    for (i, name) in frames.by_ref().enumerate() {
        if i == MAX_STACK_FRAMES {
            out.push('\n');
            out.push_str(TRUNCATED);
            break;
        }
        if !out.is_empty() {
            out.push('\n');
        }
        out.push_str(&bound(name, 256));
        if out.len() > MAX_STACK_CHARS {
            return bound(&out, MAX_STACK_CHARS);
        }
    }
    out
}

/// `text` cut to at most `limit` characters, ending in `[truncated]` when
/// cut (never splitting a character).
#[must_use]
pub fn bound(text: &str, limit: usize) -> String {
    if text.chars().count() <= limit {
        return text.to_owned();
    }
    let keep = limit.saturating_sub(TRUNCATED.chars().count());
    let mut out: String = text.chars().take(keep).collect();
    out.push_str(TRUNCATED);
    out
}

fn json_string(text: &str) -> String {
    ac_render::gpu::json_string(text)
}

fn json_opt_str(value: Option<&str>, limit: usize) -> String {
    value.map_or_else(|| "null".to_owned(), |v| json_string(&bound(v, limit)))
}

fn json_finite(value: Option<f32>) -> String {
    match value {
        Some(v) if v.is_finite() => format!("{v}"),
        _ => "null".to_owned(),
    }
}

/// The report: schema, time, process, version, the failure, and the GPU
/// and world facts when a window had published them.
#[must_use]
pub fn report_json(failure: &Failure, facts: Option<&PresentationFacts>, at: SystemTime) -> String {
    let mut out = String::from("{\n");
    let _ = writeln!(out, "  \"schemaVersion\": 1,");
    let _ = writeln!(
        out,
        "  \"utc\": {},",
        json_string(&ac_render::profiler::utc_iso8601(at))
    );
    let _ = writeln!(out, "  \"processId\": {},", std::process::id());
    let _ = writeln!(
        out,
        "  \"version\": {},",
        json_string(env!("CARGO_PKG_VERSION"))
    );
    let _ = writeln!(
        out,
        "  \"thread\": {},",
        json_string(&bound(&failure.thread, 128))
    );
    let _ = writeln!(
        out,
        "  \"location\": {},",
        json_opt_str(failure.location.as_deref(), 512)
    );
    let _ = writeln!(out, "  \"stack\": {},", json_string(&failure.stack));
    match facts {
        None => {
            let _ = writeln!(out, "  \"gpu\": null,");
            let _ = writeln!(out, "  \"world\": null");
        }
        Some(f) => {
            let _ = writeln!(
                out,
                "  \"gpu\": {{ \"deviceName\": {}, \"backend\": {}, \"driver\": {}, \"width\": {}, \"height\": {} }},",
                json_string(&bound(&f.device_name, 512)),
                json_string(&bound(&f.backend, 64)),
                json_string(&bound(&f.driver, 512)),
                f.width,
                f.height
            );
            let [x, y, z] = f.position.map_or([None; 3], |p| p.map(Some));
            let non_finite = [x, y, z].iter().any(|v| v.is_some_and(|v| !v.is_finite()));
            let _ = writeln!(
                out,
                "  \"world\": {{ \"cellId\": {}, \"x\": {}, \"y\": {}, \"z\": {}, \"camera\": {}, \"nonFiniteCoordinates\": {} }}",
                f.cell_id
                    .map_or_else(|| "null".to_owned(), |c| c.to_string()),
                json_finite(x),
                json_finite(y),
                json_finite(z),
                json_opt_str(
                    Some(&f.camera)
                        .filter(|c| !c.is_empty())
                        .map(String::as_str),
                    64
                ),
                non_finite
            );
        }
    }
    out.push_str("}\n");
    out
}

/// Write `json` to a new, uniquely named file in `directory`: first under
/// a temporary name, then renamed, so the directory only ever holds whole
/// reports. A report over [`MAX_REPORT_BYTES`] is refused.
pub fn write_report(directory: &Path, json: &str) -> std::io::Result<PathBuf> {
    if json.len() > MAX_REPORT_BYTES {
        return Err(std::io::Error::other("report too large"));
    }
    std::fs::create_dir_all(directory)?;
    let at = SystemTime::now();
    let nanos = at
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or(Duration::ZERO)
        .subsec_nanos();
    let stamp = ac_render::profiler::utc_iso8601(at)
        .replace([':', '-'], "")
        .replace('.', "");
    // Two reports in the same instant get different suffixes; an existing
    // report is never overwritten.
    for attempt in 0..64 {
        let identity = format!("crash-{stamp}-{}-{nanos}-{attempt}", std::process::id());
        let path = directory.join(format!("{identity}.json"));
        let temporary = directory.join(format!(".{identity}.tmp"));
        if path.exists() || temporary.exists() {
            continue;
        }
        let written = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)
            .and_then(|mut file| std::io::Write::write_all(&mut file, json.as_bytes()))
            .and_then(|()| std::fs::rename(&temporary, &path));
        match written {
            Ok(()) => return Ok(path),
            Err(error) => {
                let _ = std::fs::remove_file(&temporary);
                return Err(error);
            }
        }
    }
    Err(std::io::Error::other("no free report name"))
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.App.Tests/Diagnostics/LocalCrashReportWriterTests.cs`.
    use super::*;

    fn failure() -> Failure {
        Failure {
            thread: "session-1".into(),
            location: Some("crates/x/src/y.rs:10:5".into()),
            stack: "openac::main\nstd::rt::lang_start".into(),
        }
    }

    fn directory(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "openac-crash-{name}-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(SystemTime::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let _ = std::fs::remove_dir_all(&dir);
        dir
    }

    #[test]
    fn a_report_names_the_failure_process_and_version() {
        let json = report_json(&failure(), None, SystemTime::now());
        assert!(json.contains("\"schemaVersion\": 1"));
        assert!(json.contains(&format!("\"processId\": {}", std::process::id())));
        assert!(json.contains(env!("CARGO_PKG_VERSION")));
        assert!(json.contains("\"thread\": \"session-1\""));
        assert!(json.contains("\"location\": \"crates/x/src/y.rs:10:5\""));
        assert!(json.contains("openac::main\\nstd::rt::lang_start"));
    }

    #[test]
    fn missing_context_leaves_gpu_and_world_null() {
        let json = report_json(&failure(), None, SystemTime::now());
        assert!(json.contains("\"gpu\": null"));
        assert!(json.contains("\"world\": null"));
    }

    #[test]
    fn context_keeps_the_gpu_and_the_cell_local_position() {
        let facts = PresentationFacts {
            device_name: "AMD device".into(),
            backend: "Vulkan".into(),
            driver: "cached driver".into(),
            width: 1920,
            height: 1080,
            cell_id: Some(0xF418_0111),
            position: Some([12.25, -3.5, 160.0]),
            camera: "Chase".into(),
        };
        let json = report_json(&failure(), Some(&facts), SystemTime::now());
        assert!(json.contains("\"deviceName\": \"AMD device\""));
        assert!(json.contains("\"driver\": \"cached driver\""));
        assert!(json.contains("\"width\": 1920, \"height\": 1080"));
        assert!(json.contains(&format!("\"cellId\": {}", 0xF418_0111u32)));
        assert!(json.contains("\"x\": 12.25, \"y\": -3.5, \"z\": 160"));
        assert!(json.contains("\"nonFiniteCoordinates\": false"));
    }

    #[test]
    fn non_finite_coordinates_are_null_and_flagged() {
        let facts = PresentationFacts {
            position: Some([f32::NAN, f32::INFINITY, f32::NEG_INFINITY]),
            ..PresentationFacts::default()
        };
        let json = report_json(&failure(), Some(&facts), SystemTime::now());
        assert!(json.contains("\"cellId\": null, \"x\": null, \"y\": null, \"z\": null"));
        assert!(json.contains("\"nonFiniteCoordinates\": true"));
    }

    #[test]
    fn large_unicode_text_is_bounded_without_splitting_characters() {
        let huge = "秘密🌋".repeat(20_000);
        let facts = PresentationFacts {
            device_name: huge.clone(),
            driver: huge.clone(),
            ..PresentationFacts::default()
        };
        let mut f = failure();
        f.thread = huge.clone();
        f.location = Some(huge);
        let json = report_json(&f, Some(&facts), SystemTime::now());
        assert!(json.len() < MAX_REPORT_BYTES);
        assert!(json.contains("[truncated]"));
        assert!(!json.contains('\u{FFFD}'));
        assert_eq!(bound("abc", 3), "abc");
        let cut = bound(&"秘密🌋".repeat(10), 13);
        assert_eq!(cut.chars().count(), 13);
        assert!(cut.starts_with("秘密") && cut.ends_with(TRUNCATED));
    }

    #[test]
    fn the_stack_keeps_function_names_only_and_is_bounded() {
        let printed = "   0: openac::crash::tests::boom\n             at ./crates/openac/src/crash.rs:1:1\n   1: std::rt::lang_start\n";
        assert_eq!(
            function_stack(printed),
            "openac::crash::tests::boom\nstd::rt::lang_start"
        );
        let deep: String = (0..100).map(|i| format!("  {i}: frame_{i}\n")).collect();
        let stack = function_stack(&deep);
        assert_eq!(stack.lines().count(), MAX_STACK_FRAMES + 1);
        assert!(stack.ends_with(TRUNCATED));
    }

    #[test]
    fn the_panic_message_is_never_part_of_the_report() {
        // The report is built from the failure's thread, location and
        // stack only; there is no field a message could reach.
        let json = report_json(&failure(), None, SystemTime::now());
        assert!(!json.contains("message"));
    }

    #[test]
    fn repeated_writes_are_unique_and_leave_only_whole_reports() {
        let dir = directory("unique");
        let json = report_json(&failure(), None, SystemTime::now());
        let a = write_report(&dir, &json).unwrap();
        let b = write_report(&dir, &json).unwrap();
        assert_ne!(a, b);
        let names: Vec<String> = std::fs::read_dir(&dir)
            .unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        assert_eq!(names.len(), 2);
        assert!(
            names
                .iter()
                .all(|n| n.starts_with("crash-") && n.ends_with(".json"))
        );
        assert_eq!(std::fs::read_to_string(&a).unwrap(), json);
        assert!(write_report(&dir, &"x".repeat(MAX_REPORT_BYTES + 1)).is_err());
        let _ = std::fs::remove_dir_all(&dir);
    }
}
