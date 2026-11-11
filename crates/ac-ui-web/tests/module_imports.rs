//! The module a page loads imports nothing: everything comes in through its
//! exports. Run after `cargo build -p ac-ui-web --target
//! wasm32-unknown-unknown --release`.

use std::path::PathBuf;

/// A LEB128 unsigned number at `at`, and the position after it.
fn leb(bytes: &[u8], mut at: usize) -> (u64, usize) {
    let (mut value, mut shift) = (0u64, 0);
    loop {
        let byte = bytes[at];
        at += 1;
        value |= u64::from(byte & 0x7F) << shift;
        if byte & 0x80 == 0 {
            return (value, at);
        }
        shift += 7;
    }
}

/// Every section of a WebAssembly module: its id and its contents.
fn sections(bytes: &[u8]) -> Vec<(u8, &[u8])> {
    assert_eq!(&bytes[..4], b"\0asm", "a WebAssembly module");
    let mut at = 8;
    let mut found = Vec::new();
    while at < bytes.len() {
        let id = bytes[at];
        let (len, start) = leb(bytes, at + 1);
        let end = start + usize::try_from(len).unwrap();
        found.push((id, &bytes[start..end]));
        at = end;
    }
    found
}

#[test]
#[ignore = "reads the release build of the module"]
fn the_built_module_imports_nothing() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../target/wasm32-unknown-unknown/release/ac_ui_web.wasm");
    let bytes = std::fs::read(&path)
        .unwrap_or_else(|e| panic!("{}: {e} (build the module first)", path.display()));
    let sections = sections(&bytes);
    let imports = sections
        .iter()
        .find(|(id, _)| *id == 2)
        .map_or(0, |(_, body)| leb(body, 0).0);
    println!(
        "{}: {} bytes, {imports} imports",
        path.display(),
        bytes.len()
    );
    assert_eq!(imports, 0, "the module imports nothing");
    let exports = sections
        .iter()
        .find(|(id, _)| *id == 7)
        .map(|(_, body)| leb(body, 0).0);
    assert!(exports.is_some_and(|n| n > 20), "{exports:?} exports");
}
