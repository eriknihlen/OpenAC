//! A plugin's `plugin.json` ([`manifest`]) and the client's own version as
//! manifests name it ([`version`]).
//!
//! The client's plugin host (`ac-plugin`, which re-exports both modules) and
//! the launcher read a manifest with this one parser; the launcher links it
//! without the plugin engine.

pub mod manifest;
pub mod version;

pub use manifest::PluginManifest;
pub use version::HostVersion;
