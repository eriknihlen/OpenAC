//! The launcher's release fingerprint hashes every workspace crate it links
//! (the release workflow walks `cargo metadata` from `openac-launcher`
//! through normal and build dependencies). A launcher whose fingerprint
//! matches the release's does not update itself, so nothing the client
//! changes often may be among those crates. These tests walk the same graph.
//!
//! In particular the plugin API's current version: it moves almost every
//! release and belongs to the client, so the crate that defines it must not
//! be linked by the launcher (which reads the installed client's range from
//! its `plugin-api.json` instead).

use std::collections::{BTreeSet, HashMap, VecDeque};
use std::path::{Path, PathBuf};

use ac_host_core::json::{self as host_json, Node, Value};

fn workspace_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(2)
        .expect("crates/ac-launcher-core sits two levels below the workspace root")
        .to_path_buf()
}

fn member<'a>(node: &'a Node, name: &str) -> Option<&'a Node> {
    host_json::object_members(node)
        .ok()?
        .iter()
        .find(|member| member.name == name)
        .map(|member| &member.value)
}

fn text<'a>(node: &'a Node, name: &str) -> Option<&'a str> {
    match &member(node, name)?.value {
        Value::String(text) => Some(text),
        _ => None,
    }
}

fn items<'a>(node: &'a Node, name: &str) -> &'a [Node] {
    member(node, name).map_or(&[], |node| host_json::array_items(node).unwrap_or(&[]))
}

/// A workspace crate: its name and folder.
struct LocalCrate {
    name: String,
    directory: PathBuf,
}

/// The workspace crates the launcher links (the release workflow's walk).
fn launcher_linked_workspace_crates() -> Vec<LocalCrate> {
    let cargo = std::env::var("CARGO").unwrap_or_else(|_| "cargo".into());
    let output = std::process::Command::new(cargo)
        .args(["metadata", "--format-version", "1", "--locked", "--offline"])
        .current_dir(workspace_root())
        .output()
        .expect("cargo metadata runs");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let root = host_json::parse(&output.stdout).expect("cargo metadata prints JSON");
    let packages: HashMap<&str, &Node> = items(&root, "packages")
        .iter()
        .filter_map(|package| Some((text(package, "id")?, package)))
        .collect();
    let resolve = member(&root, "resolve").expect("a resolved graph");
    let nodes: HashMap<&str, &Node> = items(resolve, "nodes")
        .iter()
        .filter_map(|node| Some((text(node, "id")?, node)))
        .collect();
    let start = packages
        .iter()
        .find(|(_, package)| {
            text(package, "name") == Some("openac-launcher")
                && member(package, "source").is_none_or(host_json::is_null)
        })
        .map(|(id, _)| *id)
        .expect("the openac-launcher package");
    let mut linked: BTreeSet<&str> = BTreeSet::from([start]);
    let mut queue = VecDeque::from([start]);
    while let Some(id) = queue.pop_front() {
        for dependency in items(nodes[id], "deps") {
            let built = items(dependency, "dep_kinds").iter().any(|kind| {
                member(kind, "kind").is_none_or(host_json::is_null)
                    || text(kind, "kind") == Some("build")
            });
            if let Some(package) = text(dependency, "pkg")
                && built
                && linked.insert(package)
            {
                queue.push_back(package);
            }
        }
    }
    linked
        .into_iter()
        .map(|id| packages[id])
        .filter(|package| member(package, "source").is_none_or(host_json::is_null))
        .map(|package| LocalCrate {
            name: text(package, "name").unwrap().to_owned(),
            directory: Path::new(text(package, "manifest_path").unwrap())
                .parent()
                .unwrap()
                .to_path_buf(),
        })
        .collect()
}

fn rust_files(directory: &Path, out: &mut Vec<PathBuf>) {
    for entry in std::fs::read_dir(directory).into_iter().flatten().flatten() {
        let path = entry.path();
        if path.is_dir() {
            rust_files(&path, out);
        } else if path.extension().is_some_and(|extension| extension == "rs") {
            out.push(path);
        }
    }
}

/// The workspace crates whose sources (not their tests) define `name` as a
/// constant.
fn crates_defining(name: &str) -> BTreeSet<String> {
    let definition = format!("pub const {name}:");
    let mut found = BTreeSet::new();
    for entry in std::fs::read_dir(workspace_root().join("crates"))
        .unwrap()
        .flatten()
    {
        let mut files = Vec::new();
        rust_files(&entry.path().join("src"), &mut files);
        if files
            .iter()
            .any(|file| std::fs::read_to_string(file).is_ok_and(|text| text.contains(&definition)))
        {
            found.insert(entry.file_name().to_string_lossy().into_owned());
        }
    }
    found
}

#[test]
fn the_launcher_links_only_its_own_crates_and_the_four_it_shares() {
    let mut names: Vec<String> = launcher_linked_workspace_crates()
        .into_iter()
        .map(|local| local.name)
        .collect();
    names.sort();
    assert_eq!(
        names,
        [
            "ac-host-core",
            "ac-launcher-core",
            "ac-launcher-net",
            "ac-plugin-icon",
            "ac-plugin-manifest",
            "ac-plugin-version",
            "ac-proxy",
            "openac-launcher",
        ]
    );
}

#[test]
fn a_new_plugin_api_version_changes_no_crate_the_launcher_links() {
    let defining = crates_defining("API_CURRENT");
    assert_eq!(
        defining,
        BTreeSet::from(["ac-plugin-abi".to_owned()]),
        "the plugin API's current version is defined once, on the client's side"
    );
    for local in launcher_linked_workspace_crates() {
        assert!(
            !defining.contains(&local.name),
            "the launcher links {} ({}), which defines the plugin API's current version; the \
             launcher must judge plugins by the installed client's plugin-api.json",
            local.name,
            local.directory.display()
        );
    }
}
