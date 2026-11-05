//! The image files plugin markup names (`<icon file="icons/sword.png"/>`):
//! what a read of one gives ([`FileImage`]), and the host's reader of them
//! ([`FileImageLoader`]), which resolves the path inside the plugin's
//! folder, reads, decodes and builds the smaller levels away from the
//! session's thread. The session holds what a window draws in the plugin's
//! own image table ([`crate::PluginUi`]).

use std::fmt;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, PoisonError};

use crate::icon::PluginIcon;
use crate::images::DecodedImage;

/// `path` with `.light` or `.dark` put before the file's extension, or after
/// the name when it has none: the version of an image a light or a dark
/// theme draws when it is there. None when the file is already a version,
/// whichever its letter case.
#[must_use]
pub fn variant_path(path: &str, light: bool) -> Option<String> {
    let name_start = path.rfind(['/', '\\']).map_or(0, |at| at + 1);
    let dot = path[name_start..]
        .rfind('.')
        .map_or(path.len(), |at| name_start + at);
    let stem = path[name_start..dot].to_ascii_lowercase();
    if stem.ends_with(".light") || stem.ends_with(".dark") {
        return None;
    }
    let variant = if light { "light" } else { "dark" };
    Some(format!("{}.{variant}{}", &path[..dot], &path[dot..]))
}

/// An image file a plugin ships, read, decoded and given its smaller
/// levels. `path` is the path as the reader spells it (one spelling per
/// file); `version` tells one version of the file on disk from the next.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FileImage {
    /// The path in the plugin's folder.
    pub path: String,
    /// The file's version (its size and time written).
    pub version: String,
    /// The image and every smaller level below it, down to 1 x 1.
    pub levels: Vec<DecodedImage>,
    /// The file it was read from, when known, so a holder can ask whether
    /// it has changed since ([`FileImageLoader::is_current`]).
    pub full_path: Option<PathBuf>,
}

impl FileImage {
    /// The image's width and height (its first level).
    #[must_use]
    pub fn size(&self) -> (u32, u32) {
        self.levels.first().map_or((0, 0), |l| (l.width, l.height))
    }

    /// The bytes of every level.
    #[must_use]
    pub fn bytes(&self) -> u64 {
        self.levels.iter().map(|l| l.rgba.len() as u64).sum()
    }
}

/// What reading one image file came to: the image, or why there is none.
pub type FileImageResult = Result<Arc<FileImage>, String>;

/// One read of an image file, under way or done. Clones share the read.
#[derive(Debug, Clone, Default)]
pub struct FileImageRead(Arc<Mutex<Option<FileImageResult>>>);

impl FileImageRead {
    /// A read not finished yet ([`Self::finish`] finishes it).
    #[must_use]
    pub fn pending() -> Self {
        Self::default()
    }

    /// A read already done.
    #[must_use]
    pub fn finished(result: FileImageResult) -> Self {
        Self(Arc::new(Mutex::new(Some(result))))
    }

    /// Finishes the read (a second result is ignored).
    pub fn finish(&self, result: FileImageResult) {
        let mut slot = self.0.lock().unwrap_or_else(PoisonError::into_inner);
        if slot.is_none() {
            *slot = Some(result);
        }
    }

    /// Done.
    #[must_use]
    pub fn is_finished(&self) -> bool {
        self.0
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .is_some()
    }

    /// What it came to, once done.
    #[must_use]
    pub fn result(&self) -> Option<FileImageResult> {
        self.0
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }

    /// The same read.
    #[must_use]
    pub fn same(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}

/// Reads the image files plugin markup names, away from the session's
/// thread.
pub trait FileImageLoader: fmt::Debug + Send + Sync {
    /// Starts reading `path` in plugin folder `directory`, or joins the read
    /// already under way. The read never fails outright: a file that cannot
    /// be used comes back with the reason.
    fn load(&self, directory: &Path, path: &str) -> FileImageRead;

    /// Whether `path` in plugin folder `directory` is a file that could be
    /// read: how a light or dark version is looked for, so a missing one is
    /// skipped without a report. A reader that cannot tell has none.
    fn has_file(&self, directory: &Path, path: &str) -> bool {
        let _ = (directory, path);
        false
    }

    /// Reads `path` in plugin folder `directory` as a window's dock icon, at
    /// once: it follows the rules of a plugin's `icon.png`, so it is small.
    /// None when there is no such file (a reader that cannot tell has none).
    ///
    /// # Errors
    /// Why a file that is there cannot be used.
    fn read_dock_icon(&self, directory: &Path, path: &str) -> Result<Option<PluginIcon>, String> {
        let _ = (directory, path);
        Ok(None)
    }

    /// Whether `image` is still what is on disk: its file's version now is
    /// the one it was read from. One whose file cannot be looked at is not,
    /// and is read again, which says why.
    fn is_current(&self, _image: &FileImage) -> bool {
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_version_goes_before_the_extension() {
        for (path, light, expected) in [
            ("icons/sword.png", true, Some("icons/sword.light.png")),
            (
                "icons/a.b/sword.png",
                false,
                Some("icons/a.b/sword.dark.png"),
            ),
            (
                r"icons\a.b\sword.png",
                true,
                Some(r"icons\a.b\sword.light.png"),
            ),
            ("sword", true, Some("sword.light")),
            ("icons/sword.light.png", true, None),
            ("icons/sword.dark.png", true, None),
            ("icons/sword.LIGHT.png", false, None),
        ] {
            assert_eq!(variant_path(path, light).as_deref(), expected, "{path}");
        }
    }
}
