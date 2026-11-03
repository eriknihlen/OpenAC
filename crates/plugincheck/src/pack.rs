//! `plugincheck pack`: a plugin folder's release assets.

use std::path::{Path, PathBuf};

use ac_launcher_core::fsutil;
use ac_launcher_core::integrity::sha256_hex;
use ac_launcher_core::plugins::{
    ICON_FILE_NAME, InstallRules, PluginIndex, PluginManifest, plugin_release_tag, release_tag,
};

/// What `pack` wrote.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PackedRelease {
    /// The plugin id.
    pub id: String,
    /// Its version.
    pub version: String,
    /// The release tag to publish under (`wasm-v<version>`, or
    /// `wasm-<id>-v<version>` when packed into an index).
    pub tag: String,
    /// The zip.
    pub zip: PathBuf,
    /// Every asset written, the zip included.
    pub files: Vec<PathBuf>,
}

/// Why nothing was packed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PackError {
    /// A path is missing or cannot be read or written (exit code 2).
    Usage(String),
    /// The folder's manifest would be refused (exit code 1).
    Refused(String),
}

/// Finder and Explorer metadata files, left out of a package (a direct
/// install ignores them too).
fn is_metadata_file(name: &str) -> bool {
    name.eq_ignore_ascii_case(".DS_Store")
        || name.eq_ignore_ascii_case("Thumbs.db")
        || name.eq_ignore_ascii_case("desktop.ini")
        || name.starts_with("._")
}

fn io(path: &Path, error: &std::io::Error) -> PackError {
    PackError::Usage(format!("{}: {error}", path.display()))
}

/// Every file under `folder` as (`/`-separated relative path, bytes), in
/// ordinal order of the paths; links are refused.
fn collect(folder: &Path) -> Result<Vec<(String, Vec<u8>)>, PackError> {
    let mut files = Vec::new();
    let mut pending = vec![(folder.to_path_buf(), String::new())];
    while let Some((directory, prefix)) = pending.pop() {
        let entries = std::fs::read_dir(&directory).map_err(|e| io(&directory, &e))?;
        for entry in entries {
            let entry = entry.map_err(|e| io(&directory, &e))?;
            let path = entry.path();
            let name = entry.file_name().to_string_lossy().into_owned();
            if is_metadata_file(&name) {
                continue;
            }
            let metadata = std::fs::symlink_metadata(&path).map_err(|e| io(&path, &e))?;
            if fsutil::metadata_is_link(&metadata) {
                return Err(PackError::Refused(format!(
                    "'{}' is a link; a plugin package holds plain files only.",
                    path.display()
                )));
            }
            let relative = if prefix.is_empty() {
                name
            } else {
                format!("{prefix}/{name}")
            };
            if metadata.is_dir() {
                pending.push((path, relative));
            } else {
                let bytes = std::fs::read(&path).map_err(|e| io(&path, &e))?;
                files.push((relative, bytes));
            }
        }
    }
    files.sort_by(|a, b| a.0.cmp(&b.0));
    Ok(files)
}

/// Packs the plugin `folder` into `out`: `plugin.json`, `<id>-<version>.zip`
/// (the folder's files, manifest at the root), `<id>-<version>.zip.sha256`
/// and, when the folder has one, `icon.png`. The same folder always packs
/// to the same bytes.
///
/// # Errors
/// The manifest is missing or would be refused, a file cannot be read or
/// written, or the folder holds a link.
pub fn pack(folder: &Path, out: &Path) -> Result<PackedRelease, PackError> {
    pack_as(folder, out, false)
}

/// Packs as [`pack`] does, for a repository publishing several plugins:
/// the release is tagged `wasm-<id>-v<version>`, and this plugin's entry in
/// the repository's plugin index `index_file` (started when the file does
/// not exist) is set to that tag, as its stable or beta release, keeping
/// every other entry. The index is checked before anything is written.
///
/// # Errors
/// As [`pack`], and the existing index file cannot be read or would be
/// refused by the launcher.
pub fn pack_into_index(
    folder: &Path,
    out: &Path,
    index_file: &Path,
) -> Result<PackedRelease, PackError> {
    let mut index = match std::fs::read(index_file) {
        Ok(bytes) => PluginIndex::parse(&String::from_utf8_lossy(
            bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(&bytes),
        ))
        .map_err(|error| PackError::Refused(format!("{}: {error}", index_file.display())))?,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => PluginIndex::new(),
        Err(error) => return Err(io(index_file, &error)),
    };
    let packed = pack_as(folder, out, true)?;
    index
        .set_release(&packed.id, &packed.tag)
        .map_err(|error| PackError::Refused(error.to_string()))?;
    std::fs::write(index_file, index.to_json()).map_err(|e| io(index_file, &e))?;
    Ok(packed)
}

fn pack_as(folder: &Path, out: &Path, by_id: bool) -> Result<PackedRelease, PackError> {
    let manifest_path = folder.join("plugin.json");
    let manifest_bytes = std::fs::read(&manifest_path).map_err(|e| io(&manifest_path, &e))?;
    let text = String::from_utf8_lossy(
        manifest_bytes
            .strip_prefix(b"\xEF\xBB\xBF")
            .unwrap_or(&manifest_bytes),
    )
    .into_owned();
    let manifest = PluginManifest::parse(&text)
        .and_then(|manifest| manifest.validate_for_install().map(|()| manifest))
        .map_err(|error| PackError::Refused(format!("plugin.json would be refused: {error}")))?;

    let files = collect(folder)?;
    let zip_bytes = crate::zip::write(&files).map_err(|_| {
        PackError::Refused("The folder is too large for a zip without Zip64.".into())
    })?;
    std::fs::create_dir_all(out).map_err(|e| io(out, &e))?;
    let zip_name = format!("{}-{}.zip", manifest.id, manifest.version);
    let zip = out.join(&zip_name);
    let sidecar = out.join(format!("{zip_name}.sha256"));
    let mut written = Vec::new();
    let mut write = |path: PathBuf, bytes: &[u8]| -> Result<(), PackError> {
        std::fs::write(&path, bytes).map_err(|e| io(&path, &e))?;
        written.push(path);
        Ok(())
    };
    write(out.join("plugin.json"), &manifest_bytes)?;
    write(zip.clone(), &zip_bytes)?;
    write(
        sidecar,
        format!("{}  {zip_name}\n", sha256_hex(&zip_bytes)).as_bytes(),
    )?;
    let icon = folder.join(ICON_FILE_NAME);
    if icon.is_file() {
        let bytes = std::fs::read(&icon).map_err(|e| io(&icon, &e))?;
        write(out.join(ICON_FILE_NAME), &bytes)?;
    }
    let tag = if by_id {
        plugin_release_tag(&manifest.id, &manifest.version)
    } else {
        release_tag(&manifest.version)
    };
    Ok(PackedRelease {
        tag,
        id: manifest.id,
        version: manifest.version,
        zip,
        files: written,
    })
}
