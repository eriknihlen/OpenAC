//! Prints the `plugin-api.json` that `openac plugin-api` prints: the plugin
//! API versions this build's plugin host loads. The macOS bundle uses it when
//! the client it packages cannot run where it is built (an Intel build on an
//! Apple-silicon Mac without Rosetta).

fn main() {
    println!("{}", ac_plugin_abi::API_RANGE.to_file_text().trim_end());
}
