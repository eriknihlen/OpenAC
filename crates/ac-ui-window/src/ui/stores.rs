//! The settings file as the UI's stores: window layouts and the plugin
//! windows' remembered control values are both kept in it.

use std::collections::BTreeMap;

use ac_host::settings::{MarkupControlKey, SettingsStore};
use ac_plugin_ui::control_state::{ControlKey, ControlStateFile};
use ac_ui::layout_persistence::LayoutStore;

use super::WindowUi;

/// The settings file as the control-state store's file.
#[derive(Debug)]
pub struct ControlStateSettings(pub SettingsStore);

impl ControlStateFile for ControlStateSettings {
    fn load(&self, character: &str) -> Result<BTreeMap<ControlKey, String>, String> {
        Ok(self
            .0
            .try_load_markup_control_state(character)?
            .into_iter()
            .map(|(k, v)| (ControlKey::new(&k.window, &k.control, &k.kind), v))
            .collect())
    }

    fn save(&self, character: &str, values: &BTreeMap<ControlKey, String>) -> Result<(), String> {
        let values = values
            .iter()
            .map(|(k, v)| {
                (
                    MarkupControlKey::new(&k.window, &k.control, &k.kind),
                    v.clone(),
                )
            })
            .collect();
        self.0
            .save_markup_control_state(character, &values)
            .map_err(|e| e.to_string())
    }
}

/// A window UI kept in the settings file.
pub trait SettingsStores {
    /// Where window layouts and the plugin windows' control values are
    /// saved: the settings file (none: they last the session).
    fn set_layout_store(&mut self, store: Option<SettingsStore>);
}

impl SettingsStores for WindowUi {
    fn set_layout_store(&mut self, store: Option<SettingsStore>) {
        let control_state = store
            .clone()
            .map(|s| Box::new(ControlStateSettings(s)) as Box<dyn ControlStateFile>);
        self.set_stores(
            store.map(|s| Box::new(s) as Box<dyn LayoutStore>),
            control_state,
        );
    }
}
