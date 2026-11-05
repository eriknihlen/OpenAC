//! The report: one line per check, a verdict, and its text and JSON forms.

use ac_host::json::write_string;

/// The JSON report's schema version.
pub const SCHEMA_VERSION: u32 = 1;

/// What the tool ran against.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CheckMode {
    /// An unzipped plugin folder (a hand install).
    Directory,
    /// A plugin `.zip` (a release asset).
    Zip,
}

/// How one check went.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CheckStatus {
    /// Passed.
    Pass,
    /// Failed: the plugin would be refused.
    Fail,
    /// Not applicable here.
    Skip,
}

/// Whether the launcher would install the plugin (and a session load it).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CheckVerdict {
    /// Every check passed or was skipped.
    WouldInstall,
    /// At least one check failed.
    WouldBeRefused,
}

/// One line of the report. A failure's message carries both the refusal and
/// what to do about it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CheckItem {
    /// What was checked.
    pub check: &'static str,
    /// How it went.
    pub status: CheckStatus,
    /// The detail.
    pub message: String,
}

impl CheckItem {
    pub(crate) fn pass(check: &'static str, message: impl Into<String>) -> Self {
        Self {
            check,
            status: CheckStatus::Pass,
            message: message.into(),
        }
    }

    pub(crate) fn skip(check: &'static str, message: impl Into<String>) -> Self {
        Self {
            check,
            status: CheckStatus::Skip,
            message: message.into(),
        }
    }

    /// `<reason>. Fix: <remediation>`, with the reason's own full stop kept.
    pub(crate) fn fail(check: &'static str, reason: &str, remediation: &str) -> Self {
        let trimmed = reason.trim_end();
        let terminator = if trimmed.ends_with(['.', '!', '?']) {
            ""
        } else {
            "."
        };
        Self {
            check,
            status: CheckStatus::Fail,
            message: format!("{trimmed}{terminator} Fix: {remediation}"),
        }
    }
}

/// The whole report.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CheckReport {
    /// [`SCHEMA_VERSION`].
    pub schema_version: u32,
    /// Folder or zip.
    pub mode: CheckMode,
    /// The full path checked.
    pub path: String,
    /// The verdict.
    pub verdict: CheckVerdict,
    /// Every check, in order.
    pub checks: Vec<CheckItem>,
}

impl CheckReport {
    pub(crate) fn finish(mode: CheckMode, path: String, checks: Vec<CheckItem>) -> Self {
        let verdict = if checks.iter().any(|c| c.status == CheckStatus::Fail) {
            CheckVerdict::WouldBeRefused
        } else {
            CheckVerdict::WouldInstall
        };
        Self {
            schema_version: SCHEMA_VERSION,
            mode,
            path,
            verdict,
            checks,
        }
    }

    /// The checks named `check`.
    pub fn item(&self, check: &str) -> Option<&CheckItem> {
        self.checks.iter().find(|item| item.check == check)
    }

    /// The text form.
    pub fn to_human(&self) -> String {
        let mut out = format!("openAC plugincheck - {}\n", self.path);
        out.push_str(match self.mode {
            CheckMode::Directory => "Mode: unzipped plugin folder (a hand install)\n",
            CheckMode::Zip => "Mode: plugin .zip (a release asset)\n",
        });
        out.push('\n');
        for item in &self.checks {
            let tag = match item.status {
                CheckStatus::Pass => "PASS",
                CheckStatus::Fail => "FAIL",
                CheckStatus::Skip => "SKIP",
            };
            out.push_str(&format!(
                "[{tag}] {}\n       {}\n",
                item.check, item.message
            ));
        }
        out.push('\n');
        out.push_str(match self.verdict {
            CheckVerdict::WouldInstall => "Verdict: this plugin would install.\n",
            CheckVerdict::WouldBeRefused => "Verdict: this plugin would be refused.\n",
        });
        out.push('\n');
        out.push_str(
            "This checks the plugin package and its module only. It cannot check the GitHub \
             release from a local path: publish it as a release tagged wasm-v<version> (or \
             wasm-<id>-v<version> in a repository publishing several plugins), not \
             marked latest, with plugin.json, <id>-<version>.zip, its .sha256 and the optional \
             icon.png as assets.\n",
        );
        out
    }

    /// The JSON form: one line, the only thing written to standard output.
    pub fn to_json(&self) -> String {
        let mut out = format!("{{\"schemaVersion\":{},\"mode\":", self.schema_version);
        write_string(
            &mut out,
            match self.mode {
                CheckMode::Directory => "directory",
                CheckMode::Zip => "zip",
            },
        );
        out.push_str(",\"path\":");
        write_string(&mut out, &self.path);
        out.push_str(",\"verdict\":");
        write_string(
            &mut out,
            match self.verdict {
                CheckVerdict::WouldInstall => "would-install",
                CheckVerdict::WouldBeRefused => "would-be-refused",
            },
        );
        out.push_str(",\"checks\":[");
        for (index, item) in self.checks.iter().enumerate() {
            if index > 0 {
                out.push(',');
            }
            out.push_str("{\"check\":");
            write_string(&mut out, item.check);
            out.push_str(",\"status\":");
            write_string(
                &mut out,
                match item.status {
                    CheckStatus::Pass => "pass",
                    CheckStatus::Fail => "fail",
                    CheckStatus::Skip => "skip",
                },
            );
            out.push_str(",\"message\":");
            write_string(&mut out, &item.message);
            out.push('}');
        }
        out.push_str("]}\n");
        out
    }
}
