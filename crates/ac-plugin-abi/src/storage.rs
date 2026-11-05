//! Plugin storage (API group N): files under roots the client chooses,
//! named by relative `/`-separated keys (no `..`, no `\`, nothing absolute).

use crate::{wire_enum, wire_record};

wire_enum! {
    /// Which tree a key lives in.
    pub enum StorageRoot: "storage root" {
        /// The plugin's own data, kept by the client across runs.
        PluginData = 0,
        /// The files the plugin shipped with (read only).
        PluginFiles = 1,
        /// The shared VTank profile folder (needs the `profiles`
        /// permission).
        Profiles = 2,
    }
}

wire_enum! {
    /// A namespace inside a root.
    pub enum ScopeKind: "storage scope" {
        /// The root itself.
        Global = 0,
        /// `character/<name>`.
        Character = 1,
        /// `world/<name>`.
        World = 2,
    }
}

wire_record! {
    /// A root and a namespace in it.
    pub struct StoragePlace {
        /// The root.
        pub root: StorageRoot,
        /// The namespace.
        pub scope: ScopeKind,
        /// The character's or world's name (empty for global).
        pub scope_name: String,
    }
}

impl StoragePlace {
    /// The root itself.
    #[must_use]
    pub fn global(root: StorageRoot) -> Self {
        Self {
            root,
            scope: ScopeKind::Global,
            scope_name: String::new(),
        }
    }

    /// A character's namespace in `root`.
    #[must_use]
    pub fn character(root: StorageRoot, name: &str) -> Self {
        Self {
            root,
            scope: ScopeKind::Character,
            scope_name: name.to_owned(),
        }
    }

    /// A world's namespace in `root`.
    #[must_use]
    pub fn world(root: StorageRoot, name: &str) -> Self {
        Self {
            root,
            scope: ScopeKind::World,
            scope_name: name.to_owned(),
        }
    }
}

wire_record! {
    /// A key in a place (or a key prefix, for a listing or a folder).
    pub struct StorageKey {
        /// Where.
        pub place: StoragePlace,
        /// The key, `/`-separated.
        pub key: String,
    }
}

wire_record! {
    /// Replace what is stored under a key, atomically.
    pub struct StorageWrite {
        /// Where.
        pub place: StoragePlace,
        /// The key.
        pub key: String,
        /// The bytes (text is written as UTF-8 without a byte order mark).
        pub bytes: Vec<u8>,
    }
}
