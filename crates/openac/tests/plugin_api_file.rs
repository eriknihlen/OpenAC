//! `openac plugin-api` prints the `plugin-api.json` a release packages beside
//! the program: this build's plugin API range, written so the launcher's own
//! reader reads it back. The launcher judges every plugin by that file, so
//! the program, the file and the reader must agree.

use std::process::Command;

use ac_launcher_core::plugins::{PLUGIN_API_FILE, parse_plugin_api_file, read_plugin_api_file};

fn plugin_api_output() -> Vec<u8> {
    let output = Command::new(env!("CARGO_BIN_EXE_openac"))
        .arg("plugin-api")
        .output()
        .expect("openac runs");
    assert!(output.status.success(), "{output:?}");
    output.stdout
}

#[test]
fn the_printed_file_is_this_builds_plugin_api_range() {
    let printed = plugin_api_output();
    assert_eq!(
        parse_plugin_api_file(&printed),
        Some(ac_plugin::abi::API_RANGE),
        "{}",
        String::from_utf8_lossy(&printed)
    );
    assert_eq!(
        String::from_utf8(printed).unwrap().trim_end(),
        ac_plugin::abi::API_RANGE.to_file_text().trim_end()
    );
}

#[test]
fn the_printed_file_is_read_from_a_client_folder() {
    let folder = std::env::temp_dir().join(format!("openac-plugin-api-{}", std::process::id()));
    std::fs::create_dir_all(&folder).unwrap();
    std::fs::write(folder.join(PLUGIN_API_FILE), plugin_api_output()).unwrap();
    let read = read_plugin_api_file(&folder);
    std::fs::remove_dir_all(&folder).unwrap();
    assert_eq!(read, Some(ac_plugin::abi::API_RANGE));
}
