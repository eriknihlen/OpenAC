//! The client executable's icon on Windows (OpenAC `AcDream.App.csproj`
//! `ApplicationIcon`: `acdream-client.ico`).

include!("../openac-launcher/windows_icon.rs");

fn main() {
    link_windows_icon("../../assets/icons/client.ico", "client-icon");
}
