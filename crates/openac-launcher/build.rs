//! The launcher executable's icon on Windows (OpenAC
//! `AcDream.Launcher.csproj` `ApplicationIcon`: `acdream-launcher.ico`).

include!("windows_icon.rs");

fn main() {
    link_windows_icon("../../assets/icons/launcher.ico", "launcher-icon");
}
