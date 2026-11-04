//! `plugincheck`: would the launcher install this plugin, and would a
//! session load it?
//!
//! `plugincheck <path> [--json]` checks an unzipped plugin folder (a hand
//! install) or a plugin `.zip` (a release asset) against the launcher's own
//! install rules, by calling the launcher's code, never a copy of it: the
//! manifest (read by the client's parser), the zip's safety and caps, the
//! content policy, the icon and the `.sha256` sidecar. It then loads the
//! `.wasm` without running it and checks it the way a session does before
//! instantiating: the API version in its `openac.api` section against the
//! manifest's, the record layouts in its `openac.layout` section against
//! the client's, every import against the declared permissions, and the
//! exports the calling convention needs.
//!
//! `plugincheck pack <folder> <out-dir>` writes a plugin folder's release
//! assets (`plugin.json`, `<id>-<version>.zip`, its `.sha256`, `icon.png`)
//! and checks the zip it wrote. With `--index <file>`, for a repository
//! publishing several plugins, the release is tagged `wasm-<id>-v<version>`
//! and the plugin's entry is merged into the local plugin index `<file>`,
//! which the publisher uploads to the repository's `wasm-index` release.
//!
//! Exit codes: 0 would install, 1 would be refused, 2 usage or a path that
//! cannot be read.

mod pack;
mod report;
mod runner;
mod zip;

use std::io::Write;
use std::path::Path;

use ac_launcher_core::plugins::{PLUGIN_INDEX_ASSET, PLUGIN_INDEX_TAG};

pub use pack::{PackError, PackedRelease, pack, pack_into_index};
pub use report::{CheckItem, CheckMode, CheckReport, CheckStatus, CheckVerdict, SCHEMA_VERSION};
pub use runner::{UsageError, run_check};

const USAGE: &str = "usage: plugincheck <path> [--json]\n       \
                     plugincheck pack <plugin folder> <out dir> [--index <plugins-index.json>]";

/// Runs the tool with `arguments` (without the program name), writing the
/// report to `output` and problems to `error`; returns the exit code.
pub fn run(arguments: &[String], output: &mut dyn Write, error: &mut dyn Write) -> i32 {
    if arguments.first().map(String::as_str) == Some("pack") {
        return run_pack(&arguments[1..], output, error);
    }
    let mut json = false;
    let mut path: Option<&str> = None;
    for argument in arguments {
        if argument == "--json" {
            json = true;
        } else if path.is_none() {
            path = Some(argument);
        } else {
            let _ = writeln!(error, "{USAGE}");
            return 2;
        }
    }
    let Some(path) = path else {
        let _ = writeln!(error, "{USAGE}");
        return 2;
    };
    match run_check(Path::new(path)) {
        Ok(report) => {
            if json {
                let _ = output.write_all(report.to_json().as_bytes());
            } else {
                let _ = output.write_all(report.to_human().as_bytes());
            }
            exit_code(&report)
        }
        Err(usage) => {
            let _ = writeln!(error, "{usage}");
            2
        }
    }
}

fn exit_code(report: &CheckReport) -> i32 {
    match report.verdict {
        CheckVerdict::WouldInstall => 0,
        CheckVerdict::WouldBeRefused => 1,
    }
}

fn run_pack(arguments: &[String], output: &mut dyn Write, error: &mut dyn Write) -> i32 {
    let (folder, out, index) = match arguments {
        [folder, out] => (folder, out, None),
        [folder, out, flag, index] if flag == "--index" => (folder, out, Some(Path::new(index))),
        _ => {
            let _ = writeln!(error, "{USAGE}");
            return 2;
        }
    };
    let packed = match index {
        Some(index) => pack_into_index(Path::new(folder), Path::new(out), index),
        None => pack(Path::new(folder), Path::new(out)),
    };
    match packed {
        Ok(packed) => {
            let _ = writeln!(output, "Packed {} {}:", packed.id, packed.version);
            for file in &packed.files {
                let _ = writeln!(output, "  {}", file.display());
            }
            let _ = writeln!(
                output,
                "Publish them as the release tagged {} (not marked latest).",
                packed.tag
            );
            if let (Some(index), Some(newer)) = (index, &packed.index_kept_newer) {
                let _ = writeln!(
                    output,
                    "{} still names {newer}, a higher version, as {}'s latest release; it was \
                     left as it was, and the copy on the {PLUGIN_INDEX_TAG} release needs no \
                     upload.",
                    index.display(),
                    packed.id
                );
            } else if let Some(index) = index {
                let _ = writeln!(
                    output,
                    "Then upload {} as {PLUGIN_INDEX_ASSET} to the release tagged \
                     {PLUGIN_INDEX_TAG} (not marked latest), replacing the one there.",
                    index.display()
                );
            }
            let _ = writeln!(output);
            let report = match run_check(&packed.zip) {
                Ok(report) => report,
                Err(usage) => {
                    let _ = writeln!(error, "{usage}");
                    return 2;
                }
            };
            let _ = output.write_all(report.to_human().as_bytes());
            exit_code(&report)
        }
        Err(PackError::Usage(text)) => {
            let _ = writeln!(error, "{text}");
            2
        }
        Err(PackError::Refused(text)) => {
            let _ = writeln!(error, "{text}");
            1
        }
    }
}
