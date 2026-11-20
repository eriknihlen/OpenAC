//! The architecture's dependency rules over the whole resolved tree, not
//! only a crate's own manifest (`crate_graph.rs` reads those): a crate that
//! must not reach the window, the GPU, the audio device or the plugin host
//! must not reach it through any crate it depends on either.
//!
//! The tree comes from `cargo metadata`. Its resolved graph unifies features
//! across the whole workspace, which would make every crate that depends on
//! the runtime look as if it ran plugins (the client turns the runtime's
//! `host` feature on). So each crate's tree is resolved here from that crate:
//! its features propagated to the dependencies that ask for them, following
//! normal and build dependencies (not dev-dependencies). The result is an
//! upper bound of what building the crate alone compiles, not exactly it: the
//! crate's features are all on, every platform's dependencies are followed,
//! and build dependencies are merged with normal ones. A crate the tree does
//! not hold is therefore never built for it.

use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::path::Path;
use std::sync::OnceLock;

use ac_host::json::{self, Node, Value};

/// The window, GPU, audio device and window-system crates.
const DEVICES: [&str; 5] = ["winit", "wgpu", "cpal", "arboard", "raw-window-handle"];

/// The workspace crates allowed to reach a window, GPU or audio device, and
/// why. Every other workspace crate must reach none of [`DEVICES`].
const PRESENTATION: [(&str, &str); 4] = [
    ("ac-render", "the window and the GPU"),
    ("ac-audio", "the audio device"),
    (
        "openac",
        "the client binary: one binary runs headless or attaches a window to its sessions",
    ),
    ("openac-launcher", "the launcher's own window"),
];

fn member<'a>(node: &'a Node, name: &str) -> Option<&'a Node> {
    json::object_members(node)
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
    member(node, name).map_or(&[], |node| json::array_items(node).unwrap_or(&[]))
}

fn strings(node: &Node, name: &str) -> Vec<String> {
    member(node, name).map_or_else(Vec::new, string_items)
}

/// The strings of an array node.
fn string_items(node: &Node) -> Vec<String> {
    json::array_items(node)
        .unwrap_or_default()
        .iter()
        .filter_map(|item| match &item.value {
            Value::String(text) => Some(text.clone()),
            _ => None,
        })
        .collect()
}

/// A dependency's kind and platform: the kind is `None` for a normal
/// dependency, `"build"` for a build dependency; the platform is the table
/// it is listed under, `None` for every platform.
type KindAndTarget = (Option<String>, Option<String>);

fn kind_and_target(node: &Node) -> KindAndTarget {
    (
        text(node, "kind").map(str::to_owned),
        text(node, "target").map(str::to_owned),
    )
}

/// One normal or build entry of a manifest's dependency tables.
struct Dependency {
    /// The name the manifest gives it (its rename, else its package name):
    /// the name features refer to it by.
    key: String,
    optional: bool,
    default_features: bool,
    features: Vec<String>,
    /// The package (by its id in the resolve) it resolved to; `None` only
    /// for an optional dependency that nothing in the workspace turns on.
    to: Option<usize>,
}

struct Package {
    name: String,
    /// A workspace member (`workspace_members`), not a patched or vendored
    /// path crate.
    member: bool,
    features: HashMap<String, Vec<String>>,
    dependencies: Vec<Dependency>,
}

/// A crate's tree: every package it compiles, each with the package that
/// first pulled it in (`None` for the crate itself).
type Tree = BTreeMap<usize, Option<usize>>;

/// A resolved edge: the package, its library name as the dependent sees it,
/// and the kinds and platforms it is a dependency for.
type Edge<'a> = (usize, &'a str, Vec<KindAndTarget>);

struct Graph {
    packages: Vec<Package>,
    /// Each package's tree, resolved when first asked for.
    trees: Vec<OnceLock<Tree>>,
}

impl Graph {
    fn load() -> Self {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
        let cargo = std::env::var("CARGO").unwrap_or_else(|_| "cargo".into());
        let output = std::process::Command::new(cargo)
            .args([
                "metadata",
                "--format-version",
                "1",
                "--all-features",
                "--locked",
                "--offline",
            ])
            .current_dir(root)
            .output()
            .expect("cargo metadata runs");
        assert!(
            output.status.success(),
            "cargo metadata --offline failed (run `cargo fetch` first if the \
             dependencies are not downloaded):\n{}",
            String::from_utf8_lossy(&output.stderr)
        );
        let document = json::parse(&output.stdout).expect("cargo metadata prints JSON");
        let listed = items(&document, "packages");
        let index: HashMap<&str, usize> = listed
            .iter()
            .enumerate()
            .filter_map(|(i, package)| Some((text(package, "id")?, i)))
            .collect();
        let members: BTreeSet<&str> = items(&document, "workspace_members")
            .iter()
            .filter_map(|id| match &id.value {
                Value::String(id) => Some(id.as_str()),
                _ => None,
            })
            .collect();
        assert!(members.len() > 20, "workspace members read: {members:?}");
        let names: Vec<&str> = listed
            .iter()
            .map(|package| text(package, "name").unwrap_or_default())
            .collect();
        let mut edges: Vec<Vec<Edge>> = vec![Vec::new(); listed.len()];
        let resolve = member(&document, "resolve").expect("a resolved graph");
        for node in items(resolve, "nodes") {
            let from = index[text(node, "id").expect("a resolve node's id")];
            for dep in items(node, "deps") {
                let to = index[text(dep, "pkg").expect("a resolved dependency's id")];
                let library = text(dep, "name").expect("a resolved dependency's name");
                let kinds = items(dep, "dep_kinds")
                    .iter()
                    .map(kind_and_target)
                    .collect();
                edges[from].push((to, library, kinds));
            }
        }
        let packages: Vec<Package> = listed
            .iter()
            .enumerate()
            .map(|(from, package)| Package {
                name: names[from].to_owned(),
                member: text(package, "id").is_some_and(|id| members.contains(id)),
                features: member(package, "features")
                    .and_then(|node| json::object_members(node).ok())
                    .unwrap_or_default()
                    .iter()
                    .map(|feature| (feature.name.clone(), string_items(&feature.value)))
                    .collect(),
                dependencies: items(package, "dependencies")
                    .iter()
                    .filter(|dependency| text(dependency, "kind") != Some("dev"))
                    .map(|dependency| Self::entry(names[from], dependency, &edges[from], &names))
                    .collect(),
            })
            .collect();
        Self {
            trees: packages.iter().map(|_| OnceLock::new()).collect(),
            packages,
        }
    }

    /// A manifest entry of package `name`, with the package it resolved to:
    /// the resolved edge of the same kind and platform and the same rename
    /// or package name (two versions of one crate differ in one of those).
    fn entry(name: &str, dependency: &Node, edges: &[Edge], names: &[&str]) -> Dependency {
        let crate_name = text(dependency, "name").unwrap_or_default();
        let rename = text(dependency, "rename");
        let kind = kind_and_target(dependency);
        let matches: Vec<usize> = edges
            .iter()
            .filter(|(to, library, kinds)| {
                kinds.contains(&kind)
                    && match rename {
                        Some(rename) => *library == rename.replace('-', "_"),
                        None => names[*to] == crate_name,
                    }
            })
            .map(|(to, _, _)| *to)
            .collect();
        assert!(
            matches.len() <= 1,
            "{name}: its dependency {crate_name} resolves to several packages"
        );
        let optional =
            member(dependency, "optional").is_some_and(|node| node.value == Value::Bool(true));
        let to = matches.first().copied();
        assert!(
            to.is_some() || optional,
            "{name}: its dependency {crate_name} ({kind:?}) is not in the resolve"
        );
        Dependency {
            key: rename.unwrap_or(crate_name).to_owned(),
            optional,
            default_features: member(dependency, "uses_default_features")
                .is_none_or(|node| node.value == Value::Bool(true)),
            features: strings(dependency, "features"),
            to,
        }
    }

    fn find(&self, name: &str) -> usize {
        self.packages
            .iter()
            .position(|package| package.member && package.name == name)
            .unwrap_or_else(|| panic!("no workspace crate {name}"))
    }

    /// `root`'s tree, resolved once.
    fn tree(&self, root: usize) -> &Tree {
        self.trees[root].get_or_init(|| self.resolve(root))
    }

    /// Every package `root` compiles (an upper bound, see the module
    /// comment), each with the package that first pulled it in.
    fn resolve(&self, root: usize) -> Tree {
        let mut enabled: Vec<Option<BTreeSet<String>>> = vec![None; self.packages.len()];
        let mut parent = BTreeMap::from([(root, None)]);
        let mut features: BTreeSet<String> = self.packages[root].features.keys().cloned().collect();
        features.insert("default".to_owned());
        enabled[root] = Some(features);
        loop {
            let mut changed = false;
            for from in 0..self.packages.len() {
                let Some(start) = enabled[from].clone() else {
                    continue;
                };
                let package = &self.packages[from];
                // The package's own features, closed over the features they
                // turn on, and every item those features list.
                let mut on = start.clone();
                let mut pending: Vec<String> = start.into_iter().collect();
                let mut listed = Vec::new();
                while let Some(feature) = pending.pop() {
                    for item in package.features.get(&feature).into_iter().flatten() {
                        listed.push(item.clone());
                        if !item.contains('/')
                            && !item.starts_with("dep:")
                            && on.insert(item.clone())
                        {
                            pending.push(item.clone());
                        }
                    }
                }
                if enabled[from].as_ref() != Some(&on) {
                    enabled[from] = Some(on);
                    changed = true;
                }
                for dependency in &package.dependencies {
                    let key = &dependency.key;
                    let switch = format!("dep:{key}");
                    let strong = format!("{key}/");
                    let weak = format!("{key}?/");
                    let active = !dependency.optional
                        || listed
                            .iter()
                            .any(|item| *item == switch || item.starts_with(&strong));
                    if !active {
                        continue;
                    }
                    let to = dependency.to.unwrap_or_else(|| {
                        panic!(
                            "{}: its optional dependency {key} is turned on, but cargo \
                             metadata did not resolve it",
                            package.name
                        )
                    });
                    let mut wanted = dependency.features.clone();
                    if dependency.default_features {
                        wanted.push("default".to_owned());
                    }
                    wanted.extend(listed.iter().filter_map(|item| {
                        item.strip_prefix(&strong)
                            .or_else(|| item.strip_prefix(&weak))
                            .map(str::to_owned)
                    }));
                    let set = enabled[to].get_or_insert_with(|| {
                        changed = true;
                        parent.insert(to, Some(from));
                        BTreeSet::new()
                    });
                    for feature in wanted {
                        changed |= set.insert(feature);
                    }
                }
            }
            if !changed {
                return parent;
            }
        }
    }

    /// For each of `forbidden` that `root`'s tree holds, the chain that pulls
    /// it in, as `a -> b -> c` (empty when the tree holds none).
    fn reaches(&self, root: &str, forbidden: &[&str]) -> Vec<String> {
        let tree = self.tree(self.find(root));
        let mut found = Vec::new();
        for &package in tree.keys() {
            if !forbidden.contains(&self.packages[package].name.as_str()) {
                continue;
            }
            let mut chain = vec![self.packages[package].name.clone()];
            let mut at = package;
            while let Some(&Some(up)) = tree.get(&at) {
                chain.push(self.packages[up].name.clone());
                at = up;
            }
            chain.reverse();
            found.push(chain.join(" -> "));
        }
        found
    }

    fn workspace_crates(&self) -> Vec<&str> {
        self.packages
            .iter()
            .filter(|package| package.member)
            .map(|package| package.name.as_str())
            .collect()
    }
}

/// The graph, read once for all the tests.
fn graph() -> &'static Graph {
    static GRAPH: OnceLock<Graph> = OnceLock::new();
    GRAPH.get_or_init(Graph::load)
}

/// Rules the tree breaks today, each a (crate, forbidden crate, why) the
/// tests accept until it is fixed. Each must still hold: a fixed one is
/// dropped from this list.
const KNOWN_EXCEPTIONS: [(&str, &str, &str); 2] = [
    (
        "ac-render",
        "ac-plugin",
        "ac-render asks for the runtime's `host` feature (the session runner), which brings the plugin host",
    ),
    (
        "ac-render",
        "wasmtime",
        "ac-render asks for the runtime's `host` feature (the session runner), which brings the plugin host",
    ),
];

fn assert_reaches_none(graph: &Graph, roots: &[&str], forbidden: &[&str]) {
    let mut failures = Vec::new();
    for root in roots {
        for name in forbidden {
            let excepted = KNOWN_EXCEPTIONS
                .iter()
                .any(|(from, to, _)| from == root && to == name);
            let found = graph.reaches(root, &[name]);
            if excepted {
                assert!(
                    !found.is_empty(),
                    "{root} no longer reaches {name}: drop it from KNOWN_EXCEPTIONS"
                );
            } else {
                failures.extend(found);
            }
        }
    }
    assert!(
        failures.is_empty(),
        "forbidden dependencies:\n{}",
        failures.join("\n")
    );
}

/// The resolution matches what cargo builds: the runtime without its `host`
/// feature (as the window crates use it) does not run plugins, with it does.
#[test]
fn the_tree_is_resolved_per_crate_not_unified() {
    let graph = graph();
    assert!(!graph.reaches("ac-runtime", &["wasmtime"]).is_empty());
    assert!(graph.reaches("ac-ui", &["wasmtime"]).is_empty());
    assert!(!graph.reaches("ac-ui", &["ac-runtime"]).is_empty());
    assert!(!graph.reaches("ac-render", &["wgpu"]).is_empty());
}

/// Invariant 1: the runtime owns all game and session state and never
/// depends on a window, GPU or audio device, through anything it uses.
#[test]
fn the_runtime_reaches_no_window_gpu_or_audio_device() {
    let graph = graph();
    assert_reaches_none(
        graph,
        &["ac-runtime"],
        &[
            &DEVICES[..],
            &["ac-render", "ac-audio", "ac-ui", "ac-ui-window", "egui"],
        ]
        .concat(),
    );
}

/// The network, protocol, bit-reading and dat crates, and the game logic
/// the runtime is built from, reach no device either.
#[test]
fn the_core_crates_reach_no_window_gpu_or_audio_device() {
    let graph = graph();
    assert_reaches_none(
        graph,
        &[
            "ac-net",
            "ac-proto",
            "ac-bits",
            "ac-dat",
            "ac-core",
            "ac-physics",
            "ac-motion",
            "ac-nav",
            "ac-plugin",
            "ac-plugin-ui",
        ],
        &DEVICES,
    );
}

/// Only the presentation crates reach a window, GPU or audio device; any
/// other workspace crate, a new one included, must not.
#[test]
fn only_the_presentation_crates_reach_a_device() {
    let graph = graph();
    let allowed: Vec<&str> = PRESENTATION.iter().map(|(name, _)| *name).collect();
    let roots: Vec<&str> = graph
        .workspace_crates()
        .into_iter()
        .filter(|name| !allowed.contains(name))
        .collect();
    assert!(roots.len() > 20, "workspace crates read: {roots:?}");
    assert_reaches_none(graph, &roots, &DEVICES);
    for name in allowed {
        assert!(
            !graph.reaches(name, &DEVICES).is_empty(),
            "{name} no longer reaches a device: drop it from PRESENTATION"
        );
    }
}

/// Plugins are loaded by the session, never by the window: the window's
/// crates do not reach the plugin host through anything they use (the
/// renderer does today, through the runtime's `host` feature: see
/// [`KNOWN_EXCEPTIONS`]; its own code does not, `crate_graph.rs`).
#[test]
fn the_window_crates_do_not_reach_the_plugin_host() {
    let graph = graph();
    assert_reaches_none(
        graph,
        &["ac-render", "ac-ui", "ac-ui-window", "ac-audio"],
        &["ac-plugin", "wasmtime"],
    );
}

/// The session's plugin UI state reaches neither the host nor the window.
#[test]
fn the_plugin_ui_state_reaches_neither_the_host_nor_the_window() {
    let graph = graph();
    assert_reaches_none(
        graph,
        &["ac-plugin-ui"],
        &[
            "ac-plugin",
            "ac-plugin-abi",
            "wasmtime",
            "ac-runtime",
            "ac-render",
            "ac-ui",
            "winit",
            "wgpu",
        ],
    );
}

/// The plugin host reaches nothing of the window, nor the runtime.
#[test]
fn the_plugin_host_reaches_nothing_of_the_window() {
    let graph = graph();
    assert_reaches_none(
        graph,
        &["ac-plugin"],
        &[
            "ac-render",
            "ac-ui",
            "ac-ui-window",
            "ac-audio",
            "winit",
            "wgpu",
            "cpal",
            "ac-runtime",
        ],
    );
}
