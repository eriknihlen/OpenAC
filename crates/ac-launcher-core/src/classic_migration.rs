//! Copies the profiles of a classic VirindiTank and UtilityBelt setup into
//! the shared profile folder of an install, in the layout MossTank reads:
//! `vtank/mosstank/` with `profiles/`, `metas/`, `navs/`, `loot/` and the
//! UtilityBelt tree under `ub/` (OpenAC `ClassicMigration`). The originals
//! are only ever read. The launcher's import dialog and `actool
//! migrate-classic` share it.

use std::cmp::Ordering;
use std::ffi::OsStr;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

/// The plugin the imported profiles are for.
pub const MOSSTANK_PLUGIN_ID: &str = ac_host_core::paths::VTANK_PLUGIN_ID;

/// The folder MossTank keeps everything a person edits in.
pub const MOSSTANK_FOLDER: &str = "mosstank";

/// The UtilityBelt profile folders MossTank reads, at the plugin, server
/// and character tiers.
const UB_PROFILE_FOLDERS: [&str; 5] = ["autovendor", "equip", "itemgiver", "maps", "dungeonmaps"];

const UB_ALIAS_FILE_NAME: &str = "aliases.txt";

/// What happens to one file found in a classic installation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ClassicMigrationAction {
    /// Copied to its new place.
    Copy,
    /// Copied over a different file that was already there.
    Overwrite,
    /// The same bytes are already in the new place.
    AlreadyPresent,
    /// A different file is already in the new place and is kept.
    Conflict,
    /// Not brought over; the reason says why.
    Skip,
}

/// One file of a classic installation and where it goes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClassicMigrationItem {
    /// The file in the classic installation.
    pub source: PathBuf,
    /// Where it goes, or `None` when it is skipped.
    pub destination: Option<PathBuf>,
    /// What is done with it.
    pub action: ClassicMigrationAction,
    /// Why it is skipped, or `None`.
    pub reason: Option<&'static str>,
}

impl ClassicMigrationItem {
    fn skip(source: PathBuf, reason: &'static str) -> Self {
        Self {
            source,
            destination: None,
            action: ClassicMigrationAction::Skip,
            reason: Some(reason),
        }
    }
}

/// The VirindiTank folder of a usual Windows install, or `None` when none
/// is found.
pub fn find_virindi_tank() -> Option<PathBuf> {
    if !cfg!(windows) {
        return None;
    }
    let mut candidates = vec![PathBuf::from(r"C:\Games\VirindiPlugins\VirindiTank")];
    for variable in ["ProgramFiles(x86)", "ProgramFiles"] {
        if let Some(folder) = std::env::var_os(variable).filter(|value| !value.is_empty()) {
            candidates.push(
                PathBuf::from(folder)
                    .join("VirindiPlugins")
                    .join("VirindiTank"),
            );
        }
    }
    candidates.into_iter().find(|path| path.is_dir())
}

/// The UtilityBelt folder of a usual Windows install, or `None` when none
/// is found.
pub fn find_utility_belt() -> Option<PathBuf> {
    if !cfg!(windows) {
        return None;
    }
    let candidate = std::env::home_dir()
        .filter(|home| !home.as_os_str().is_empty())?
        .join("Documents")
        .join("Decal Plugins")
        .join("UtilityBelt");
    candidate.is_dir().then_some(candidate)
}

/// Every file of the two classic folders and what would be done with it.
/// Nothing is written.
///
/// `vtank_profiles_directory` is the install's shared profile folder;
/// `overwrite` says whether a different file already there is replaced.
pub fn plan(
    virindi_tank_directory: Option<&Path>,
    utility_belt_directory: Option<&Path>,
    vtank_profiles_directory: &Path,
    overwrite: bool,
) -> io::Result<Vec<ClassicMigrationItem>> {
    let target = vtank_profiles_directory.join(MOSSTANK_FOLDER);
    let mut items = Vec::new();
    if let Some(source) = virindi_tank_directory {
        plan_virindi_tank(source, &target, overwrite, &mut items)?;
    }
    if let Some(source) = utility_belt_directory {
        plan_utility_belt(source, &target.join("ub"), overwrite, &mut items)?;
    }
    Ok(items)
}

/// Copies every planned file that has somewhere to go.
pub fn apply(items: &[ClassicMigrationItem]) -> io::Result<()> {
    for item in items {
        let overwrite = match item.action {
            ClassicMigrationAction::Copy => false,
            ClassicMigrationAction::Overwrite => true,
            _ => continue,
        };
        let Some(destination) = item.destination.as_deref() else {
            continue;
        };
        if let Some(parent) = destination.parent() {
            fs::create_dir_all(parent)?;
        }
        // A plain copy never replaces a file that appeared after the plan.
        if !overwrite && destination.exists() {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                format!("The file '{}' already exists.", destination.display()),
            ));
        }
        fs::copy(&item.source, destination)?;
    }
    Ok(())
}

/// One line naming what is done with `item`.
pub fn describe(item: &ClassicMigrationItem) -> String {
    let source = item.source.display();
    let destination = item
        .destination
        .as_deref()
        .map_or_else(String::new, |path| path.display().to_string());
    match item.action {
        ClassicMigrationAction::Copy => format!("copy       {source} -> {destination}"),
        ClassicMigrationAction::Overwrite => format!("overwrite  {source} -> {destination}"),
        ClassicMigrationAction::AlreadyPresent => format!("present    {destination}"),
        ClassicMigrationAction::Conflict => format!("conflict   {destination} differs; kept"),
        ClassicMigrationAction::Skip => {
            format!("skip       {source}: {}", item.reason.unwrap_or_default())
        }
    }
}

/// The counts of a plan in one sentence; `applied` says whether the plan
/// was carried out or only looked at.
pub fn summarize(items: &[ClassicMigrationItem], applied: bool) -> String {
    let count = |wanted: &[ClassicMigrationAction]| {
        items
            .iter()
            .filter(|item| wanted.contains(&item.action))
            .count()
    };
    format!(
        "{} {} file(s); {} already present, {} conflict(s), {} skipped.",
        if applied { "Copied" } else { "Would copy" },
        count(&[
            ClassicMigrationAction::Copy,
            ClassicMigrationAction::Overwrite
        ]),
        count(&[ClassicMigrationAction::AlreadyPresent]),
        count(&[ClassicMigrationAction::Conflict]),
        count(&[ClassicMigrationAction::Skip]),
    )
}

/// Whether any planned file is a conflict.
pub fn has_conflicts(items: &[ClassicMigrationItem]) -> bool {
    items
        .iter()
        .any(|item| item.action == ClassicMigrationAction::Conflict)
}

fn plan_virindi_tank(
    source: &Path,
    target: &Path,
    overwrite: bool,
    items: &mut Vec<ClassicMigrationItem>,
) -> io::Result<()> {
    // VirindiTank keeps every profile flat in its own folder; the
    // extension says which MossTank folder it belongs in.
    for file in files(source, false)? {
        let Some(name) = file.file_name().map(OsStr::to_os_string) else {
            continue;
        };
        let extension = file
            .extension()
            .map(|extension| extension.to_string_lossy().to_lowercase());
        let folder = match extension.as_deref() {
            Some("usd" | "cdf") => Some("profiles"),
            Some("met") => Some("metas"),
            Some("nav") => Some("navs"),
            Some("utl") => Some("loot"),
            _ => None,
        };
        let Some(folder) = folder else {
            let reason = if equals_ignore_case(&name.to_string_lossy(), "gameinfodb.ugd") {
                "MossTank downloads a current game database itself"
            } else {
                "not a profile"
            };
            items.push(ClassicMigrationItem::skip(file, reason));
            continue;
        };
        let destination = target.join(folder).join(name);
        items.push(classify(file, destination, overwrite)?);
    }
    Ok(())
}

fn plan_utility_belt(
    source: &Path,
    target: &Path,
    overwrite: bool,
    items: &mut Vec<ClassicMigrationItem>,
) -> io::Result<()> {
    for file in files(source, true)? {
        let Ok(relative) = file.strip_prefix(source) else {
            continue;
        };
        let segments: Vec<&OsStr> = relative.iter().collect();
        if segments.is_empty() {
            continue;
        }
        let item = match ub_skip_reason(&segments) {
            None => {
                let destination = segments
                    .iter()
                    .fold(target.to_path_buf(), |path, segment| path.join(segment));
                classify(file.clone(), destination, overwrite)?
            }
            Some(reason) => ClassicMigrationItem::skip(file.clone(), reason),
        };
        items.push(item);
    }
    Ok(())
}

/// Why a file of the UtilityBelt tree is not brought over, or `None` when
/// it is. The tree is mirrored as it is, keeping its plugin, server and
/// character tiers, but only for the files MossTank reads.
fn ub_skip_reason(segments: &[&OsStr]) -> Option<&'static str> {
    let text: Vec<String> = segments
        .iter()
        .map(|segment| segment.to_string_lossy().into_owned())
        .collect();
    let name = text.last()?.as_str();
    if equals_ignore_case(name, "settings.json") || ends_with_ignore_case(name, ".settings.json") {
        return Some("settings files have a different format in MossTank; set these again in game");
    }

    // A profile folder sits at the plugin tier (index 0), the server tier
    // (index 1) or the character tier (index 2).
    for segment in text.iter().take((text.len() - 1).min(3)) {
        if UB_PROFILE_FOLDERS
            .iter()
            .any(|folder| equals_ignore_case(segment, folder))
        {
            return None;
        }
    }

    let is_character_aliases = text.len() == 3 && equals_ignore_case(name, UB_ALIAS_FILE_NAME);
    let is_named_aliases = text.len() == 2
        && equals_ignore_case(&text[0], "profiles")
        && ends_with_ignore_case(name, &format!(".{UB_ALIAS_FILE_NAME}"));
    if is_character_aliases || is_named_aliases {
        None
    } else {
        Some("not used by MossTank")
    }
}

fn classify(
    source: PathBuf,
    destination: PathBuf,
    overwrite: bool,
) -> io::Result<ClassicMigrationItem> {
    let action = if !destination.is_file() {
        ClassicMigrationAction::Copy
    } else if same_content(&source, &destination)? {
        ClassicMigrationAction::AlreadyPresent
    } else if overwrite {
        ClassicMigrationAction::Overwrite
    } else {
        ClassicMigrationAction::Conflict
    };
    Ok(ClassicMigrationItem {
        source,
        destination: Some(destination),
        action,
        reason: None,
    })
}

fn same_content(left: &Path, right: &Path) -> io::Result<bool> {
    if fs::metadata(left)?.len() != fs::metadata(right)?.len() {
        return Ok(false);
    }
    Ok(fs::read(left)? == fs::read(right)?)
}

/// The files of `folder` (and, with `recursive`, of every folder beneath
/// it), ordered by full path ignoring case. Hidden files are listed; a
/// sub-folder that is gone by the time it is read is passed over.
fn files(folder: &Path, recursive: bool) -> io::Result<Vec<PathBuf>> {
    let mut found = Vec::new();
    let mut pending = vec![folder.to_path_buf()];
    let mut first = true;
    while let Some(current) = pending.pop() {
        let entries = match fs::read_dir(&current) {
            Ok(entries) => entries,
            Err(error) if !first && error.kind() == io::ErrorKind::NotFound => continue,
            Err(error) => return Err(error),
        };
        first = false;
        for entry in entries {
            let entry = entry?;
            let path = entry.path();
            if is_folder(&entry, &path) {
                if recursive {
                    pending.push(path);
                }
            } else {
                found.push(path);
            }
        }
    }
    found.sort_by(|a, b| compare_ignore_case(&a.to_string_lossy(), &b.to_string_lossy()));
    Ok(found)
}

/// A link to a folder counts as a folder, as the folder attribute says on
/// Windows.
#[cfg(windows)]
fn is_folder(entry: &fs::DirEntry, _path: &Path) -> bool {
    use std::os::windows::fs::FileTypeExt;
    entry
        .file_type()
        .is_ok_and(|kind| kind.is_dir() || kind.is_symlink_dir())
}

/// A link is followed to what it names.
#[cfg(not(windows))]
fn is_folder(entry: &fs::DirEntry, path: &Path) -> bool {
    match entry.file_type() {
        Ok(kind) if !kind.is_symlink() => kind.is_dir(),
        _ => path.is_dir(),
    }
}

/// A character's simple upper case (one character to one).
fn upper(c: char) -> char {
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(single), None) => single,
        _ => c,
    }
}

/// Text compared character by character by upper case, then by number.
fn compare_ignore_case(left: &str, right: &str) -> Ordering {
    left.chars().map(upper).cmp(right.chars().map(upper))
}

fn equals_ignore_case(left: &str, right: &str) -> bool {
    compare_ignore_case(left, right) == Ordering::Equal
}

fn ends_with_ignore_case(text: &str, suffix: &str) -> bool {
    let count = suffix.chars().count();
    let total = text.chars().count();
    total >= count
        && text
            .chars()
            .skip(total - count)
            .map(upper)
            .eq(suffix.chars().map(upper))
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TempDir(PathBuf);

    impl TempDir {
        fn new() -> Self {
            let path = std::env::temp_dir()
                .join("openac-classic-migration-tests")
                .join(crate::ids::unique_hex32());
            fs::create_dir_all(&path).unwrap();
            Self(path)
        }

        fn create(&self, parts: &[&str]) -> PathBuf {
            let path = parts
                .iter()
                .fold(self.0.clone(), |path, part| path.join(part));
            fs::create_dir_all(&path).unwrap();
            path
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn write(folder: &Path, relative: &str, text: &str) {
        let path = folder.join(relative);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, text).unwrap();
    }

    /// Every file beneath `folder`, relative, with `/`, in ordinal order.
    fn tree(folder: &Path) -> Vec<String> {
        let mut out: Vec<String> = files(folder, true)
            .unwrap()
            .iter()
            .map(|path| {
                path.strip_prefix(folder)
                    .unwrap()
                    .to_string_lossy()
                    .replace('\\', "/")
            })
            .collect();
        out.sort();
        out
    }

    // OpenAC ClassicMigrationTests.VirindiTankProfilesGoToTheFolderOfTheirKind.
    #[test]
    fn virindi_tank_profiles_go_to_the_folder_of_their_kind() {
        let directory = TempDir::new();
        let vtank = directory.create(&["VirindiTank"]);
        write(&vtank, "Hunter.usd", "settings");
        write(&vtank, "--Bob_Coldeve.usd", "character settings");
        write(&vtank, "Coldeve_Bob.cdf", "uTank2 CDF 1.0");
        write(&vtank, "Hunt.met", "meta");
        write(&vtank, "Route.nav", "nav");
        write(&vtank, "Loot.utl", "loot");
        write(&vtank, "gameinfodb.ugd", "db");
        write(&vtank, "uTank2.dll", "code");
        let profiles = directory.create(&["openac", "vtank"]);

        let items = plan(Some(&vtank), None, &profiles, false).unwrap();
        apply(&items).unwrap();

        assert_eq!(
            tree(&profiles),
            [
                "mosstank/loot/Loot.utl",
                "mosstank/metas/Hunt.met",
                "mosstank/navs/Route.nav",
                "mosstank/profiles/--Bob_Coldeve.usd",
                "mosstank/profiles/Coldeve_Bob.cdf",
                "mosstank/profiles/Hunter.usd",
            ]
        );
        assert_eq!(
            fs::read_to_string(profiles.join("mosstank/profiles/Hunter.usd")).unwrap(),
            "settings"
        );
        let reasons: Vec<(String, &str)> = items
            .iter()
            .filter(|item| item.action == ClassicMigrationAction::Skip)
            .map(|item| {
                (
                    item.source
                        .file_name()
                        .unwrap()
                        .to_string_lossy()
                        .into_owned(),
                    item.reason.unwrap(),
                )
            })
            .collect();
        assert_eq!(
            reasons,
            [
                (
                    "gameinfodb.ugd".to_owned(),
                    "MossTank downloads a current game database itself"
                ),
                ("uTank2.dll".to_owned(), "not a profile"),
            ]
        );
    }

    // OpenAC ClassicMigrationTests.UtilityBeltProfileFoldersKeepTheirTiers.
    #[test]
    fn utility_belt_profile_folders_keep_their_tiers() {
        let directory = TempDir::new();
        let ub = directory.create(&["UtilityBelt"]);
        write(&ub, "autovendor/Shared.utl", "plugin tier");
        write(&ub, "Coldeve/equip/Server.json", "server tier");
        write(&ub, "Coldeve/Bob/autovendor/Bob.utl", "character tier");
        write(&ub, "Coldeve/Bob/aliases.txt", "hi = /s hi");
        write(&ub, "profiles/group.aliases.txt", "gg = /f gg");
        write(&ub, "maps/markers.csv", "markers");
        write(&ub, "dungeonmaps/visited/0x01AB.json", "visited");
        write(&ub, "itemgiver/Give.utl", "give");
        write(&ub, "settings.json", "{}");
        write(&ub, "profiles/default.settings.json", "{}");
        write(&ub, "Coldeve/Bob/settings.json", "{}");
        write(&ub, "logs/ub.log", "log");
        let profiles = directory.create(&["openac", "vtank"]);

        let items = plan(None, Some(&ub), &profiles, false).unwrap();
        apply(&items).unwrap();

        assert_eq!(
            tree(&profiles.join("mosstank").join("ub")),
            [
                "Coldeve/Bob/aliases.txt",
                "Coldeve/Bob/autovendor/Bob.utl",
                "Coldeve/equip/Server.json",
                "autovendor/Shared.utl",
                "dungeonmaps/visited/0x01AB.json",
                "itemgiver/Give.utl",
                "maps/markers.csv",
                "profiles/group.aliases.txt",
            ]
        );
        assert_eq!(
            items
                .iter()
                .filter(|item| item.action == ClassicMigrationAction::Skip)
                .count(),
            4
        );
    }

    // OpenAC ClassicMigrationTests.ADifferentFileAlreadyThereIsKeptUnlessOverwriteIsAsked.
    #[test]
    fn a_different_file_already_there_is_kept_unless_overwrite_is_asked() {
        let directory = TempDir::new();
        let vtank = directory.create(&["VirindiTank"]);
        write(&vtank, "Hunt.met", "classic");
        write(&vtank, "Same.met", "same");
        let profiles = directory.create(&["openac", "vtank"]);
        let metas = directory.create(&["openac", "vtank", "mosstank", "metas"]);
        write(&metas, "Hunt.met", "edited in OpenAC");
        write(&metas, "Same.met", "same");

        let kept = plan(Some(&vtank), None, &profiles, false).unwrap();
        apply(&kept).unwrap();

        assert_eq!(
            kept.iter().map(|item| item.action).collect::<Vec<_>>(),
            [
                ClassicMigrationAction::Conflict,
                ClassicMigrationAction::AlreadyPresent
            ]
        );
        assert_eq!(
            fs::read_to_string(metas.join("Hunt.met")).unwrap(),
            "edited in OpenAC"
        );

        apply(&plan(Some(&vtank), None, &profiles, true).unwrap()).unwrap();

        assert_eq!(
            fs::read_to_string(metas.join("Hunt.met")).unwrap(),
            "classic"
        );
        assert_eq!(
            fs::read_to_string(vtank.join("Hunt.met")).unwrap(),
            "classic"
        );
    }

    #[test]
    fn the_lines_and_summary_name_each_outcome() {
        let item = |action, destination: Option<&str>, reason| ClassicMigrationItem {
            source: PathBuf::from("src"),
            destination: destination.map(PathBuf::from),
            action,
            reason,
        };
        let items = [
            item(ClassicMigrationAction::Copy, Some("dst"), None),
            item(ClassicMigrationAction::Overwrite, Some("dst"), None),
            item(ClassicMigrationAction::AlreadyPresent, Some("dst"), None),
            item(ClassicMigrationAction::Conflict, Some("dst"), None),
            item(ClassicMigrationAction::Skip, None, Some("not a profile")),
        ];
        assert_eq!(
            items.iter().map(describe).collect::<Vec<_>>(),
            [
                "copy       src -> dst",
                "overwrite  src -> dst",
                "present    dst",
                "conflict   dst differs; kept",
                "skip       src: not a profile",
            ]
        );
        assert_eq!(
            summarize(&items, false),
            "Would copy 2 file(s); 1 already present, 1 conflict(s), 1 skipped."
        );
        assert_eq!(
            summarize(&items, true),
            "Copied 2 file(s); 1 already present, 1 conflict(s), 1 skipped."
        );
        assert!(has_conflicts(&items));
    }

    #[test]
    fn names_compare_ignoring_case() {
        assert!(equals_ignore_case("GameInfoDB.UGD", "gameinfodb.ugd"));
        assert!(ends_with_ignore_case(
            "Default.Settings.JSON",
            ".settings.json"
        ));
        assert!(!ends_with_ignore_case("json", ".settings.json"));
        // `_` sits between the upper and lower case letters.
        assert_eq!(compare_ignore_case("a.nav", "_x.nav"), Ordering::Less);
    }
}
