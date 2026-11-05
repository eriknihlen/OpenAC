//! Every startup option is documented in one place: each `OPENAC_*`
//! environment variable named anywhere in the workspace's sources must
//! appear in `docs/launch-options.md` (OpenAC keeps the same census over
//! its `building-and-running.md`).

use std::collections::BTreeSet;
use std::path::{Path, PathBuf};

fn workspace_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(2)
        .expect("crates/openac sits two levels below the workspace root")
        .to_path_buf()
}

fn rust_files(dir: &Path, out: &mut Vec<PathBuf>) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            if path.file_name().is_some_and(|n| n == "target") {
                continue;
            }
            rust_files(&path, out);
        } else if path.extension().is_some_and(|e| e == "rs") {
            out.push(path);
        }
    }
}

/// The `OPENAC_*` names in a source text: inside string literals, letters,
/// digits and underscores after the prefix.
fn variables(text: &str) -> BTreeSet<String> {
    let mut found = BTreeSet::new();
    let mut rest = text;
    while let Some(at) = rest.find("\"OPENAC_") {
        let name: String = rest[at + 1..]
            .chars()
            .take_while(|c| c.is_ascii_uppercase() || c.is_ascii_digit() || *c == '_')
            .collect();
        if name.len() > "OPENAC_".len() && rest[at + 1 + name.len()..].starts_with('"') {
            found.insert(name);
        }
        rest = &rest[at + 1..];
    }
    found
}

#[test]
fn every_environment_variable_is_documented() {
    let root = workspace_root();
    let doc = std::fs::read_to_string(root.join("docs").join("launch-options.md"))
        .expect("docs/launch-options.md exists");
    let mut files = Vec::new();
    rust_files(&root.join("crates"), &mut files);
    assert!(!files.is_empty());
    let mut missing = Vec::new();
    let mut seen = BTreeSet::new();
    for file in files {
        let text = std::fs::read_to_string(&file).unwrap_or_default();
        for name in variables(&text) {
            if seen.insert(name.clone()) && !doc.contains(&format!("`{name}`")) {
                missing.push(format!("{name} ({})", file.display()));
            }
        }
    }
    assert!(
        seen.len() > 10,
        "the census found only {seen:?}; is it reading the sources?"
    );
    assert!(missing.is_empty(), "undocumented: {missing:#?}");
}

#[test]
fn the_census_reads_names_inside_string_literals_only() {
    let found = variables(
        r#"let a = env("OPENAC_GPU_ADAPTER"); // OPENAC_NOT_A_LITERAL
           const B: &str = "OPENAC_FRAME_PROF"; let c = "OPENAC_DAT_DIR/x";"#,
    );
    assert_eq!(
        found.into_iter().collect::<Vec<_>>(),
        ["OPENAC_FRAME_PROF", "OPENAC_GPU_ADAPTER"]
    );
}
