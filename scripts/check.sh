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
    # The panels are also a module for a web page: it must build for the web (its tests read the
    # release build to check that it imports nothing).
    cargo clippy -p ac-ui-web --target wasm32-unknown-unknown -- -D warnings
    cargo build -p ac-ui-web --target wasm32-unknown-unknown --release
    # The game logic, data and protocol crates hold no platform code: keep them building for
    # WebAssembly. The runtime builds there without its `host` feature (sessions, sockets and the
    # plugin host).
    cargo check --target wasm32-unknown-unknown \
        -p ac-bits -p ac-clock -p ac-core -p ac-dat -p ac-host -p ac-host-core -p ac-motion \
        -p ac-nav -p ac-net -p ac-physics -p ac-plugin-abi -p ac-plugin-icon \
        -p ac-plugin-manifest -p ac-plugin-ui -p ac-plugin-version -p ac-proto \
        -p ac-remote-wire -p ac-ui -p ac-ui-window -p ac-vfx -p openac-plugin
    cargo check -p ac-runtime --no-default-features --target wasm32-unknown-unknown
else
    echo "wasm32-unknown-unknown not installed; skipping the WebAssembly builds."
fi
# The default is assigned on its own: macOS's /bin/sh reads the apostrophe inside `${...:-...}`
# as an opening quote.
default_dat_dir="${USERPROFILE:-$HOME}/Documents/Asheron's Call"
dat_dir="${OPENAC_DAT_DIR:-$default_dat_dir}"
if [ -f "$dat_dir/client_portal.dat" ]; then
    echo "Retail dats found in $dat_dir; running ignored tests too."
    cargo test --workspace -- --include-ignored
    # A test that is not ignored must pass on a machine without the game: run the tests again
    # with every OPENAC_* variable cleared (OPENAC_DAT_DIR and the tests' opt-ins) and the user's
    # folders (home, application data, the XDG folders), where the dats and the launcher's install
    # record are looked for, pointed at an empty folder. A fixed path such as C:\Turbine cannot be
    # hidden this way. OPENAC_LAUNCHER_FINGERPRINT is kept: it is read when the launcher is
    # compiled, and the build is reused (only the environment differs). Without dats the run below
    # is such a run.
    echo "Running the tests again without the retail dats."
    empty_home="$PWD/target/no-game-data-home"
    rm -rf "$empty_home"
    mkdir -p "$empty_home"
    (
        real_home="${USERPROFILE:-$HOME}"
        # cargo and rustup find their own folders under the home folder unless told where.
        export CARGO_HOME="${CARGO_HOME:-$real_home/.cargo}"
        export RUSTUP_HOME="${RUSTUP_HOME:-$real_home/.rustup}"
        for name in $(env | sed -n 's/^\(OPENAC_[A-Za-z0-9_]*\)=.*/\1/p'); do
            if [ "$name" != OPENAC_LAUNCHER_FINGERPRINT ]; then
                unset "$name"
            fi
        done
        for name in HOME USERPROFILE LOCALAPPDATA APPDATA \
            XDG_DATA_HOME XDG_CONFIG_HOME XDG_CACHE_HOME XDG_STATE_HOME; do
            export "$name=$empty_home"
        done
        cargo test --workspace
    )
else
    echo "No retail dats in $dat_dir; skipping the real-dat tests."
    cargo test --workspace
fi
