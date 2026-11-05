//! The API doc census: the documentation a plugin author reads
//! is kept in step with the API. OpenAC
//! `tests/AcDream.HostParity.Tests/PluginApiDocumentationTests.cs` checks
//! its published guide (`docs/plugin-api.md`) against the host parity
//! census: every member a windowless client cannot fill is named, and none
//! that both fill is still warned about. Here the author's documentation is
//! the import table's doc comments (`ac-plugin-abi` `table.rs`, which also
//! carries the window census) and the SDK's rustdoc, so the census checks
//! both against the table: an import without docs, or one the SDK reaches
//! only through an undocumented function, fails.

use std::path::{Path, PathBuf};

use openac_plugin::Import;

/// The two plumbing imports every call uses; the SDK calls them from its
/// glue, never from a function an author calls.
const PLUMBING: [Import; 2] = [Import::AbiReplyRead, Import::AbiPanic];

/// Words with which a doc says an import answers differently without a
/// window.
const WINDOW_WORDS: [&str; 3] = ["without a window", "with no window", "no window"];

/// Every import says what it does (OpenAC: a member missing from the guide
/// fails the census).
#[test]
fn every_import_is_documented_in_the_table() {
    let missing: Vec<&str> = Import::ALL
        .iter()
        .filter(|import| import.doc().trim().is_empty())
        .map(|import| import.name())
        .collect();
    assert!(
        missing.is_empty(),
        "the import table does not document: {}",
        missing.join(", ")
    );
}

/// `TheGuideNamesEverySeamAWindowlessClientCannotFill`: an import whose
/// answer depends on a window says so in its doc.
#[test]
fn the_table_names_the_window_for_every_import_that_needs_one() {
    let silent: Vec<&str> = Import::ALL
        .iter()
        .filter(|import| import.needs_window() && !import.doc().contains("window"))
        .map(|import| import.name())
        .collect();
    assert!(
        silent.is_empty(),
        "the import table does not tell an author that these answer differently without a \
         window: {}",
        silent.join(", ")
    );
}

/// `TheGuideDoesNotStillWarnAboutAClosedDifference`: an import that answers
/// the same with or without a window does not say it answers differently.
#[test]
fn the_table_does_not_warn_about_a_window_an_import_does_not_need() {
    let stale: Vec<&str> = Import::ALL
        .iter()
        .filter(|import| {
            !import.needs_window() && {
                let doc = import.doc().replace('\n', "");
                WINDOW_WORDS.iter().any(|words| doc.contains(words))
            }
        })
        .map(|import| import.name())
        .collect();
    assert!(
        stale.is_empty(),
        "the import table says these answer differently without a window, but the window \
         census says they do not: {}",
        stale.join(", ")
    );
}

/// The SDK's source files an author's calls go through (not the platform
/// glue, not the fake host).
fn sdk_sources() -> Vec<(PathBuf, String)> {
    fn walk(directory: &Path, out: &mut Vec<(PathBuf, String)>) {
        for entry in std::fs::read_dir(directory).expect("the SDK's sources") {
            let path = entry.expect("a source entry").path();
            if path.is_dir() {
                walk(&path, out);
            } else if path.extension().is_some_and(|e| e == "rs") {
                let name = path.file_name().unwrap().to_string_lossy().into_owned();
                if name.starts_with("sys") || name == "testing.rs" || name == "__export.rs" {
                    continue;
                }
                let text = std::fs::read_to_string(&path).expect("a source file");
                out.push((path, text));
            }
        }
    }
    let mut out = Vec::new();
    walk(&Path::new(env!("CARGO_MANIFEST_DIR")).join("src"), &mut out);
    out
}

/// Whether `line` names `variant` as the import it calls.
fn calls(line: &str, variant: &str) -> bool {
    let named = |marker: &str| {
        line.match_indices(marker).any(|(at, _)| {
            line[at + marker.len()..]
                .chars()
                .next()
                .is_none_or(|c| !c.is_alphanumeric() && c != '_')
        })
    };
    named(&format!("calls::{variant}"))
        || (line.contains("ui_call!(") && named(&format!(", {variant},")))
}

/// The function a line sits in: its signature line's index.
fn enclosing_fn(lines: &[&str], at: usize) -> Option<usize> {
    (0..=at).rev().find(|&i| {
        let line = lines[i].trim_start();
        line.starts_with("pub fn ")
            || line.starts_with("pub(crate) fn ")
            || line.starts_with("fn ")
            || line.starts_with("pub const fn ")
    })
}

/// Whether the function at `signature` is public and documented.
fn documented_pub_fn(lines: &[&str], signature: usize) -> bool {
    if !lines[signature].trim_start().starts_with("pub fn ")
        && !lines[signature].trim_start().starts_with("pub const fn ")
    {
        return false;
    }
    lines[..signature]
        .iter()
        .rev()
        .map(|l| l.trim_start())
        .find(|l| !l.starts_with("#["))
        .is_some_and(|l| l.starts_with("///"))
}

/// The name of the function whose signature is `line`.
fn fn_name(line: &str) -> &str {
    let after = &line[line.find("fn ").map_or(0, |at| at + 3)..];
    after
        .split(|c: char| !c.is_alphanumeric() && c != '_')
        .next()
        .unwrap_or("")
}

/// Whether some line matching `called` sits in a public, documented
/// function.
fn reached_from_documented(sources: &[(PathBuf, String)], called: impl Fn(&str) -> bool) -> bool {
    sources.iter().any(|(_, text)| {
        let lines: Vec<&str> = text.lines().collect();
        lines.iter().enumerate().any(|(at, line)| {
            called(line) && enclosing_fn(&lines, at).is_some_and(|s| documented_pub_fn(&lines, s))
        })
    })
}

/// Every import an author can reach is reached through a public, documented
/// SDK function (the SDK's rustdoc is the guide), directly or through one
/// private helper such a function calls.
#[test]
fn every_import_is_reached_by_a_documented_sdk_function() {
    let sources = sdk_sources();
    let mut missing = Vec::new();
    for import in Import::ALL.iter().filter(|i| !PLUMBING.contains(i)) {
        let variant = format!("{import:?}");
        let helpers: Vec<String> = sources
            .iter()
            .flat_map(|(_, text)| {
                let lines: Vec<&str> = text.lines().collect();
                lines
                    .iter()
                    .enumerate()
                    .filter(|(_, line)| calls(line, &variant))
                    .filter_map(|(at, _)| enclosing_fn(&lines, at))
                    .map(|s| fn_name(lines[s]).to_owned())
                    .collect::<Vec<_>>()
            })
            .collect();
        let reached = reached_from_documented(&sources, |line| calls(line, &variant))
            || helpers.iter().any(|helper| {
                let call = format!(".{helper}(");
                reached_from_documented(&sources, |line| line.contains(&call))
            });
        if !reached {
            missing.push(import.name());
        }
    }
    assert!(
        missing.is_empty(),
        "no public, documented SDK function reaches: {}",
        missing.join(", ")
    );
}

/// The census itself: it finds a call, and refuses an undocumented or
/// private function.
#[test]
fn the_census_tells_a_documented_call_from_an_undocumented_one() {
    let text = "\
    /// Does it.
    #[must_use]
    pub fn documented(&mut self) -> bool {
        self.call::<calls::HostSetting>(&())
    }

    pub fn undocumented(&mut self) {
        let reply = ui_call!(self, UiPanelShow, ui_panel_show, &request);
    }

    /// Private.
    fn private(&mut self) {
        self.call::<calls::HostLog>(&())
    }";
    let lines: Vec<&str> = text.lines().collect();
    let at = |variant: &str| {
        let at = lines.iter().position(|l| calls(l, variant)).unwrap();
        documented_pub_fn(&lines, enclosing_fn(&lines, at).unwrap())
    };
    assert!(at("HostSetting"));
    assert!(!at("UiPanelShow"));
    assert!(!at("HostLog"));
    assert!(!calls(
        "self.call::<calls::HostSettings>(&())",
        "HostSetting"
    ));
}
