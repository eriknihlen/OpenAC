//! `migrate-classic [--vtank <dir>] [--ub <dir>] [--root <dir>]
//! [--overwrite] [--dry-run]`: copies a classic VirindiTank and
//! UtilityBelt setup into an install (OpenAC `ClassicMigrationCommand`).

use std::ffi::OsString;
use std::io::Write;
use std::path::PathBuf;

use ac_host::{PathOverrides, PathSet, SystemPlatform};
use ac_launcher_core::classic_migration;

const USAGE: &str = "usage: actool migrate-classic [--vtank <dir>] [--ub <dir>] [--root <openac-root>] [--overwrite] [--dry-run]";

/// Finds a classic folder when none is named.
type FindFolder = fn() -> Option<PathBuf>;

/// Runs the command over `args` (after `migrate-classic`); the exit code.
pub fn run(args: &[OsString], output: &mut dyn Write, error: &mut dyn Write) -> u8 {
    run_with(
        args,
        output,
        error,
        [
            classic_migration::find_virindi_tank,
            classic_migration::find_utility_belt,
        ],
    )
}

/// [`run`], finding the VirindiTank and UtilityBelt folders with `find`.
fn run_with(
    args: &[OsString],
    output: &mut dyn Write,
    error: &mut dyn Write,
    find: [FindFolder; 2],
) -> u8 {
    match run_inner(args, output, error, find) {
        Ok(code) => code,
        Err(failure) => {
            let _ = writeln!(error, "actool: {failure}");
            1
        }
    }
}

fn run_inner(
    args: &[OsString],
    output: &mut dyn Write,
    error: &mut dyn Write,
    [find_virindi_tank, find_utility_belt]: [FindFolder; 2],
) -> Result<u8, Box<dyn std::error::Error>> {
    let mut vtank = None;
    let mut ub = None;
    let mut root = None;
    let mut overwrite = false;
    let mut dry_run = false;
    let mut index = 0;
    while index < args.len() {
        let has_value = index + 1 < args.len();
        match args[index].to_str() {
            Some("--vtank") if has_value => {
                index += 1;
                vtank = Some(PathBuf::from(&args[index]));
            }
            Some("--ub") if has_value => {
                index += 1;
                ub = Some(PathBuf::from(&args[index]));
            }
            Some("--root") if has_value => {
                index += 1;
                root = Some(args[index].to_string_lossy().into_owned());
            }
            Some("--overwrite") => overwrite = true,
            Some("--dry-run") => dry_run = true,
            _ => {
                writeln!(error, "{USAGE}")?;
                return Ok(2);
            }
        }
        index += 1;
    }

    let vtank = vtank.or_else(find_virindi_tank);
    let ub = ub.or_else(find_utility_belt);
    if vtank.is_none() && ub.is_none() {
        writeln!(
            error,
            "No VirindiTank or UtilityBelt folder was found; name one with --vtank or --ub."
        )?;
        writeln!(error, "{USAGE}")?;
        return Ok(2);
    }

    for (path, name) in [(&vtank, "VirindiTank"), (&ub, "UtilityBelt")] {
        if let Some(path) = path
            && !path.is_dir()
        {
            writeln!(
                error,
                "The {name} folder {} does not exist.",
                path.display()
            )?;
            return Ok(2);
        }
    }

    let paths = PathSet::resolve(
        &PathOverrides {
            root_directory: root,
            ..PathOverrides::default()
        },
        &SystemPlatform,
    )?;
    let profiles = paths.vtank_profiles_directory();
    let none = || "(none)".to_owned();
    let shown =
        |path: &Option<PathBuf>| path.as_ref().map_or_else(none, |p| p.display().to_string());
    writeln!(output, "VirindiTank: {}", shown(&vtank))?;
    writeln!(output, "UtilityBelt: {}", shown(&ub))?;
    writeln!(
        output,
        "OpenAC:      {}",
        profiles.join(classic_migration::MOSSTANK_FOLDER).display()
    )?;
    writeln!(output)?;

    let items = classic_migration::plan(vtank.as_deref(), ub.as_deref(), &profiles, overwrite)?;
    for item in &items {
        writeln!(output, "{}", classic_migration::describe(item))?;
    }
    if !dry_run {
        classic_migration::apply(&items)?;
    }

    writeln!(output)?;
    writeln!(output, "{}", classic_migration::summarize(&items, !dry_run))?;
    if classic_migration::has_conflicts(&items) {
        writeln!(
            output,
            "Conflicting files were kept; use --overwrite to replace them."
        )?;
    }
    Ok(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Scratch(PathBuf);

    impl Scratch {
        fn new() -> Self {
            let path = std::env::temp_dir()
                .join("actool-tests")
                .join(ac_launcher_core::ids::unique_hex32());
            std::fs::create_dir_all(&path).unwrap();
            Self(path)
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn command(args: &[&OsString]) -> (u8, String, String) {
        let args: Vec<OsString> = args.iter().map(|&arg| arg.clone()).collect();
        let (mut output, mut error) = (Vec::new(), Vec::new());
        // The machine's own classic folders stay out of the test.
        let code = run_with(&args, &mut output, &mut error, [|| None, || None]);
        (
            code,
            String::from_utf8(output).unwrap(),
            String::from_utf8(error).unwrap(),
        )
    }

    // OpenAC ClassicMigrationCommandTests.ADryRunWritesNothing.
    #[test]
    fn a_dry_run_writes_nothing() {
        let scratch = Scratch::new();
        let vtank = scratch.0.join("VirindiTank");
        std::fs::create_dir_all(&vtank).unwrap();
        std::fs::write(vtank.join("Hunt.met"), "meta").unwrap();
        let root = scratch.0.join("openac");

        let (code, output, _) = command(&[
            &"--vtank".into(),
            &vtank.clone().into_os_string(),
            &"--root".into(),
            &root.clone().into_os_string(),
            &"--dry-run".into(),
        ]);

        assert_eq!(code, 0);
        assert!(output.contains("Would copy 1 file(s)"), "{output}");
        assert!(!root.exists());
    }

    // OpenAC ClassicMigrationCommandTests.AMissingSourceFolderIsRefused.
    #[test]
    fn a_missing_source_folder_is_refused() {
        let scratch = Scratch::new();

        let (code, _, error) = command(&[
            &"--vtank".into(),
            &scratch.0.join("missing").into_os_string(),
            &"--root".into(),
            &scratch.0.clone().into_os_string(),
        ]);

        assert_eq!(code, 2);
        assert!(error.contains("does not exist"), "{error}");
    }

    #[test]
    fn an_unknown_or_unfinished_argument_prints_the_usage() {
        for args in [vec!["--frobnicate"], vec!["--vtank"]] {
            let args: Vec<OsString> = args.into_iter().map(OsString::from).collect();
            let (code, _, error) = command(&args.iter().collect::<Vec<_>>());
            assert_eq!(code, 2);
            assert!(
                error.starts_with("usage: actool migrate-classic"),
                "{error}"
            );
        }
    }

    #[test]
    fn no_folder_named_or_found_is_refused() {
        let (code, _, error) = command(&[]);
        assert_eq!(code, 2);
        assert!(
            error.starts_with(
                "No VirindiTank or UtilityBelt folder was found; name one with --vtank or --ub."
            ),
            "{error}"
        );
    }

    #[test]
    fn an_import_copies_and_reports_conflicts() {
        let scratch = Scratch::new();
        let vtank = scratch.0.join("VirindiTank");
        std::fs::create_dir_all(&vtank).unwrap();
        std::fs::write(vtank.join("Hunt.met"), "classic").unwrap();
        std::fs::write(vtank.join("Route.nav"), "nav").unwrap();
        let root = scratch.0.join("openac");
        let profiles = root.join("plugins").join("acdream.mosstank").join("vtank");
        let metas = profiles.join("mosstank").join("metas");
        std::fs::create_dir_all(&metas).unwrap();
        std::fs::write(metas.join("Hunt.met"), "edited").unwrap();
        let args = [
            "--vtank".into(),
            vtank.into_os_string(),
            "--root".into(),
            root.clone().into_os_string(),
        ];

        let (code, output, _) = command(&args.iter().collect::<Vec<_>>());

        assert_eq!(code, 0);
        assert!(
            output.contains("Copied 1 file(s); 0 already present, 1 conflict(s), 0 skipped."),
            "{output}"
        );
        assert!(output.contains("Conflicting files were kept; use --overwrite to replace them."));
        assert_eq!(
            std::fs::read_to_string(metas.join("Hunt.met")).unwrap(),
            "edited"
        );
        assert!(profiles.join("mosstank/navs/Route.nav").is_file());
    }
}
