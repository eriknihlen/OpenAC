//! The window spells the plugin key numbers itself (it does not link the
//! plugin ABI, `ac-plugin`'s `crate_graph` test): every key a plugin can
//! name maps from the window key of the same name to the same number
//! (OpenAC `AppHotkeyRegistry.MapKey`).

use ac_plugin::abi::keys::PluginKey;
use ac_ui::input::chord::key_from_name;
use ac_ui::plugin::hotkeys::plugin_key;

/// The window's name for the key a plugin key names (Silk.NET's names,
/// which OpenAC's `MapKey` pairs with).
fn window_name(key: PluginKey) -> String {
    let name = key.name();
    if let Some(rest) = name.strip_prefix("Numpad") {
        return format!("Keypad{rest}");
    }
    match name {
        "Grave" => "GraveAccent".to_owned(),
        other => other.to_owned(),
    }
}

#[test]
fn every_plugin_key_maps_from_its_window_key() {
    for &key in PluginKey::ALL {
        if key == PluginKey::Unknown {
            continue;
        }
        let name = window_name(key);
        let window_key =
            key_from_name(&name).unwrap_or_else(|| panic!("the window has no key {name}"));
        assert_eq!(plugin_key(window_key), key.code(), "{name}");
    }
}
