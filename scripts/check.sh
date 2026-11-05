#!/usr/bin/env sh
# Format, lint and test the whole workspace. This is the project's CI; run it before every commit.
# When the retail dat files are present (OPENAC_DAT_DIR, else Documents/Asheron's Call under the
# user profile), the ignored real-dat conformance tests run too.
set -eu
cd "$(dirname "$0")/.."
cargo fmt --all --check
cargo clippy --workspace --all-targets -- -D warnings
# Plugins are built for WebAssembly; the crates they link must build for it too.
if rustup target list --installed | grep -qx 'wasm32-unknown-unknown'; then
    cargo build -p openac-plugin-compat --target wasm32-unknown-unknown
else
    echo "wasm32-unknown-unknown not installed; skipping the plugin-crate WebAssembly build."
fi
# The default is assigned on its own: macOS's /bin/sh reads the apostrophe inside `${...:-...}`
# as an opening quote.
default_dat_dir="${USERPROFILE:-$HOME}/Documents/Asheron's Call"
dat_dir="${OPENAC_DAT_DIR:-$default_dat_dir}"
if [ -f "$dat_dir/client_portal.dat" ]; then
    echo "Retail dats found in $dat_dir; running ignored tests too."
    cargo test --workspace -- --include-ignored
else
    echo "No retail dats in $dat_dir; skipping the real-dat tests."
    cargo test --workspace
fi
