//! Helpers shared by the integration tests.
#![allow(dead_code)]

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use ac_host::{KnownFolder, Os, Platform};

/// A path under the temp directory no other test uses.
pub fn unique_temp_path(prefix: &str, extension: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let count = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!(
        "openac-{prefix}-{}-{nanos}-{count}{extension}",
        std::process::id()
    ))
}

/// A file deleted when dropped.
pub struct TempFile {
    pub path: PathBuf,
}

impl TempFile {
    /// Writes `contents` to a fresh temp file.
    pub fn with(prefix: &str, contents: &str) -> Self {
        let path = unique_temp_path(prefix, ".json");
        std::fs::write(&path, contents).expect("write temp file");
        Self { path }
    }

    /// Reserves a fresh path without creating the file.
    pub fn reserve(prefix: &str, extension: &str) -> Self {
        Self {
            path: unique_temp_path(prefix, extension),
        }
    }

    pub fn path_string(&self) -> String {
        self.path.to_string_lossy().into_owned()
    }
}

impl Drop for TempFile {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.path);
    }
}

/// A directory removed with its contents when dropped.
pub struct TempDir {
    pub path: PathBuf,
}

impl TempDir {
    pub fn new(prefix: &str) -> Self {
        let path = unique_temp_path(prefix, "");
        std::fs::create_dir_all(&path).expect("create temp dir");
        Self { path }
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

/// A platform whose every fact the test sets.
pub struct FixturePlatform {
    pub os: Os,
    pub current_dir: PathBuf,
    pub user_profile: Option<PathBuf>,
    pub application_data: Option<PathBuf>,
    pub local_application_data: Option<PathBuf>,
    pub variables: HashMap<String, String>,
    /// Small files by path, as the pointer reader sees them.
    pub files: HashMap<PathBuf, String>,
}

impl FixturePlatform {
    pub fn new(os: Os) -> Self {
        Self {
            os,
            current_dir: std::env::temp_dir(),
            user_profile: Some(std::env::temp_dir().join("home")),
            application_data: Some(std::env::temp_dir().join("roaming")),
            local_application_data: Some(std::env::temp_dir().join("local")),
            variables: HashMap::new(),
            files: HashMap::new(),
        }
    }

    pub fn with_var(mut self, name: &str, value: &str) -> Self {
        self.variables.insert(name.to_owned(), value.to_owned());
        self
    }
}

impl Platform for FixturePlatform {
    fn os(&self) -> Os {
        self.os
    }

    fn current_dir(&self) -> Option<PathBuf> {
        Some(self.current_dir.clone())
    }

    fn var(&self, name: &str) -> Option<String> {
        self.variables.get(name).cloned()
    }

    fn known_folder(&self, folder: KnownFolder) -> Option<PathBuf> {
        match folder {
            KnownFolder::UserProfile => self.user_profile.clone(),
            KnownFolder::ApplicationData => self.application_data.clone(),
            KnownFolder::LocalApplicationData => self.local_application_data.clone(),
        }
    }

    fn read_text_file(&self, path: &Path) -> Option<String> {
        self.files.get(path).cloned()
    }
}

/// `{"version":1,"sessions":[...]}`.
pub fn configuration_with(sessions: &[String]) -> String {
    format!("{{\"version\":1,\"sessions\":[{}]}}", sessions.join(","))
}

/// A complete normal-play session with an optional extra member.
pub fn session(id: &str, credential_reference: &str, extra: Option<&str>) -> String {
    let suffix = extra.map(|extra| format!(",{extra}")).unwrap_or_default();
    format!(
        "{{\"id\":\"{id}\",\"endpoint\":{{\"host\":\"127.0.0.1\",\"port\":9000}},\
         \"account\":\"account\",\"character\":{{\"index\":0}},\
         \"policy\":{{\"id\":\"idle\"}},\"credential\":\
         {{\"provider\":\"environment\",\"reference\":\"{credential_reference}\"}}{suffix}}}"
    )
}
