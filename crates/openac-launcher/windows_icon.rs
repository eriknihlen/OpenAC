// The executable's icon on Windows (OpenAC's `ApplicationIcon`), shared by
// the build scripts of `openac-launcher` and `openac` (`include!`d there).
//
// An `.ico` file is turned into a compiled resource file (`.res`): one
// `RT_ICON` resource per image and the `RT_GROUP_ICON` directory naming
// them, which the MSVC linker takes as an input like an object file. No
// resource compiler is run and nothing but Rust is involved.

/// Writes `<out_dir>/<name>.res` from the icon at `icon` and links it into
/// the crate's executables, when building for Windows with the MSVC linker.
fn link_windows_icon(icon: &str, name: &str) {
    println!("cargo:rerun-if-changed={icon}");
    let windows = std::env::var("CARGO_CFG_TARGET_OS").is_ok_and(|os| os == "windows");
    let msvc = std::env::var("CARGO_CFG_TARGET_ENV").is_ok_and(|env| env == "msvc");
    if !windows || !msvc {
        return;
    }
    let ico = std::fs::read(icon).unwrap_or_else(|error| panic!("{icon}: {error}"));
    let res = icon_resource(&ico).unwrap_or_else(|error| panic!("{icon}: {error}"));
    let out = std::path::PathBuf::from(std::env::var("OUT_DIR").expect("cargo sets OUT_DIR"))
        .join(format!("{name}.res"));
    std::fs::write(&out, res).unwrap_or_else(|error| panic!("{}: {error}", out.display()));
    println!("cargo:rustc-link-arg-bins={}", out.display());
}

fn u16_at(bytes: &[u8], at: usize) -> Result<u16, String> {
    bytes
        .get(at..at + 2)
        .map(|b| u16::from_le_bytes([b[0], b[1]]))
        .ok_or_else(|| "the icon file is cut short".to_owned())
}

fn u32_at(bytes: &[u8], at: usize) -> Result<u32, String> {
    bytes
        .get(at..at + 4)
        .map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
        .ok_or_else(|| "the icon file is cut short".to_owned())
}

/// One resource: its 32-byte header (numeric type and name, US English),
/// its data, padded to four bytes.
fn push_resource(out: &mut Vec<u8>, kind: u16, id: u16, flags: u16, data: &[u8]) {
    out.extend_from_slice(&(data.len() as u32).to_le_bytes());
    out.extend_from_slice(&32u32.to_le_bytes());
    out.extend_from_slice(&[0xFF, 0xFF]);
    out.extend_from_slice(&kind.to_le_bytes());
    out.extend_from_slice(&[0xFF, 0xFF]);
    out.extend_from_slice(&id.to_le_bytes());
    out.extend_from_slice(&0u32.to_le_bytes()); // data version
    out.extend_from_slice(&flags.to_le_bytes());
    out.extend_from_slice(&0x0409u16.to_le_bytes()); // language
    out.extend_from_slice(&0u32.to_le_bytes()); // version
    out.extend_from_slice(&0u32.to_le_bytes()); // characteristics
    out.extend_from_slice(data);
    while !out.len().is_multiple_of(4) {
        out.push(0);
    }
}

/// The `.res` file for an `.ico` file.
fn icon_resource(ico: &[u8]) -> Result<Vec<u8>, String> {
    const RT_ICON: u16 = 3;
    const RT_GROUP_ICON: u16 = 14;
    const MOVEABLE_DISCARDABLE: u16 = 0x1010;
    const MOVEABLE_PURE_DISCARDABLE: u16 = 0x1030;
    if u16_at(ico, 0)? != 0 || u16_at(ico, 2)? != 1 {
        return Err("not an icon file".to_owned());
    }
    let count = u16_at(ico, 4)?;
    if count == 0 {
        return Err("the icon file holds no images".to_owned());
    }
    // The empty resource every .res file starts with.
    let mut out = Vec::new();
    out.extend_from_slice(&0u32.to_le_bytes());
    out.extend_from_slice(&32u32.to_le_bytes());
    out.extend_from_slice(&[0xFF, 0xFF, 0, 0, 0xFF, 0xFF, 0, 0]);
    out.extend_from_slice(&[0; 16]);

    let mut group = Vec::new();
    group.extend_from_slice(&0u16.to_le_bytes());
    group.extend_from_slice(&1u16.to_le_bytes());
    group.extend_from_slice(&count.to_le_bytes());
    for index in 0..count {
        let entry = 6 + usize::from(index) * 16;
        let head = ico
            .get(entry..entry + 4)
            .ok_or_else(|| "the icon file is cut short".to_owned())?;
        let planes = u16_at(ico, entry + 4)?.max(1);
        let bits = u16_at(ico, entry + 6)?;
        let size = u32_at(ico, entry + 8)?;
        let offset = u32_at(ico, entry + 12)? as usize;
        let image = ico
            .get(offset..offset + size as usize)
            .ok_or_else(|| "an image runs past the end of the icon file".to_owned())?;
        let id = index + 1;
        push_resource(&mut out, RT_ICON, id, MOVEABLE_DISCARDABLE, image);
        group.extend_from_slice(head);
        group.extend_from_slice(&planes.to_le_bytes());
        group.extend_from_slice(&bits.to_le_bytes());
        group.extend_from_slice(&size.to_le_bytes());
        group.extend_from_slice(&id.to_le_bytes());
    }
    push_resource(&mut out, RT_GROUP_ICON, 1, MOVEABLE_PURE_DISCARDABLE, &group);
    Ok(out)
}
