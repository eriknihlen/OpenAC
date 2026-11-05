//! What a release payload contains, which platform it is for, and the
//! Windows path rules every installed name must also satisfy.

/// The client: graphical and windowless in one program.
pub const OPENAC: &str = "openac";

/// The launcher itself (a launcher payload).
pub const LAUNCHER: &str = "openac-launcher";

/// `.exe` for a Windows runtime identifier, nothing otherwise.
pub fn suffix_for_rid(rid: &str) -> &'static str {
    if rid.starts_with("win-") { ".exe" } else { "" }
}

/// `.exe` on Windows, nothing otherwise.
pub fn suffix_for_current_os() -> &'static str {
    if cfg!(windows) { ".exe" } else { "" }
}

/// The program that runs a graphical session (OpenAC ships a separate
/// `AcDream.App`, or `acdream-client` on macOS; here it is `openac`).
pub fn graphical_host_for_rid(_rid: &str) -> &'static str {
    OPENAC
}

/// The program that runs a windowless session (OpenAC's
/// `acdream-headless`; here `openac run`).
pub fn headless_host_for_rid(_rid: &str) -> &'static str {
    OPENAC
}

/// The executables a payload carries.
pub fn executables_for_payload(rid: &str, launcher_payload: bool) -> Vec<String> {
    required_for_payload(rid, launcher_payload)
}

/// The executables whose absence invalidates a payload.
pub fn required_for_payload(rid: &str, launcher_payload: bool) -> Vec<String> {
    let suffix = suffix_for_rid(rid);
    if launcher_payload {
        vec![format!("{LAUNCHER}{suffix}")]
    } else {
        vec![format!("{OPENAC}{suffix}")]
    }
}

/// `os-arch` for the running program (`win-x64`, `linux-arm64`, ...).
pub fn detect_rid() -> Option<String> {
    let os = if cfg!(windows) {
        "windows"
    } else if cfg!(target_os = "linux") {
        "linux"
    } else if cfg!(target_os = "macos") {
        "macos"
    } else {
        "other"
    };
    detect_rid_for(os, std::env::consts::ARCH)
}

/// `os-arch` from an operating system (`windows`, `linux`, `macos`) and a
/// Rust architecture name (`x86_64`, `aarch64`). `None` for any other.
pub fn detect_rid_for(os: &str, architecture: &str) -> Option<String> {
    let os = match os {
        "windows" => "win",
        "linux" => "linux",
        "macos" => "osx",
        _ => return None,
    };
    let architecture = match architecture {
        "x86_64" => "x64",
        "aarch64" => "arm64",
        _ => return None,
    };
    Some(format!("{os}-{architecture}"))
}

/// A runtime identifier: lowercase letters, digits and `-`, starting with a
/// letter, at most 64 characters.
pub fn is_valid_rid(rid: &str) -> bool {
    !rid.is_empty()
        && rid.len() <= 64
        && rid.starts_with(|c: char| c.is_ascii_lowercase())
        && rid
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-')
}

/// Whether a path segment is a name Windows reserves for a device (`CON`,
/// `NUL`, `COM1`, `LPT¹`, ...), with or without an extension.
pub fn is_windows_device_name(segment: &str) -> bool {
    let stem = segment.split('.').next().unwrap_or_default();
    let upper = stem.to_ascii_uppercase();
    if matches!(
        upper.as_str(),
        "CON" | "PRN" | "AUX" | "NUL" | "CLOCK$" | "CONIN$" | "CONOUT$"
    ) {
        return true;
    }
    let characters: Vec<char> = stem.chars().collect();
    if characters.len() != 4 || !(upper.starts_with("COM") || upper.starts_with("LPT")) {
        return false;
    }
    matches!(characters[3], '1'..='9' | '\u{b9}' | '\u{b2}' | '\u{b3}')
}

#[cfg(test)]
mod tests {
    use super::*;

    // Ported from OpenAC tests/.../Updates/LauncherRuntimeIdentityTests.cs and
    // PayloadExecutableNamesTests.cs (with this client's one program name).

    #[test]
    fn mac_apple_silicon_uses_osx_arm64() {
        assert_eq!(
            detect_rid_for("macos", "aarch64").as_deref(),
            Some("osx-arm64")
        );
    }

    #[test]
    fn mac_intel_uses_osx_x64() {
        assert_eq!(
            detect_rid_for("macos", "x86_64").as_deref(),
            Some("osx-x64")
        );
    }

    #[test]
    fn other_platforms_are_refused() {
        assert_eq!(detect_rid_for("freebsd", "x86_64"), None);
        assert_eq!(detect_rid_for("linux", "riscv64"), None);
    }

    #[test]
    fn the_client_payload_requires_the_one_program() {
        for (rid, expected) in [
            ("win-x64", "openac.exe"),
            ("linux-x64", "openac"),
            ("osx-arm64", "openac"),
            ("osx-x64", "openac"),
        ] {
            assert_eq!(required_for_payload(rid, false), [expected]);
        }
        assert_eq!(
            required_for_payload("win-x64", true),
            ["openac-launcher.exe"]
        );
        for rid in ["osx-arm64", "osx-x64"] {
            assert_eq!(required_for_payload(rid, true), ["openac-launcher"]);
        }
    }

    #[test]
    fn device_names_are_recognized() {
        for name in [
            "CON",
            "nul.txt",
            "Com1",
            "LPT\u{b9}.dll",
            "CLOCK$",
            "conout$.x",
        ] {
            assert!(is_windows_device_name(name), "{name}");
        }
        for name in ["COM0", "COM10", "console", "lpt", "plugin.json"] {
            assert!(!is_windows_device_name(name), "{name}");
        }
    }

    #[test]
    fn rids_are_checked() {
        assert!(is_valid_rid("win-x64"));
        assert!(is_valid_rid("osx-x64"));
        assert!(!is_valid_rid("WIN_X64"));
        assert!(!is_valid_rid("1abc"));
        assert!(!is_valid_rid(""));
    }
}
