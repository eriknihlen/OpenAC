# Format, lint and test the whole workspace. This is the project's CI; run it before every commit.
# When the retail dat files are present (OPENAC_DAT_DIR, else Documents\Asheron's Call under the
# user profile), the ignored real-dat conformance tests run too.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
cargo fmt --all --check
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
cargo clippy --workspace --all-targets -- -D warnings
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# Plugins are built for WebAssembly; the crates they link must build for it too.
if ((rustup target list --installed) -contains 'wasm32-unknown-unknown') {
    cargo build -p openac-plugin-compat --target wasm32-unknown-unknown
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # The panels are also a module for a web page: it must build for the web (its tests read the
    # release build to check that it imports nothing).
    cargo clippy -p ac-ui-web --target wasm32-unknown-unknown -- -D warnings
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    cargo build -p ac-ui-web --target wasm32-unknown-unknown --release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # The game logic, data and protocol crates hold no platform code: keep them building for
    # WebAssembly. The runtime builds there without its `host` feature (sessions, sockets and the
    # plugin host).
    $wasmCrates = 'ac-bits', 'ac-clock', 'ac-core', 'ac-dat', 'ac-host', 'ac-host-core', 'ac-motion',
                  'ac-nav', 'ac-net', 'ac-physics', 'ac-plugin-abi', 'ac-plugin-icon',
                  'ac-plugin-manifest', 'ac-plugin-ui', 'ac-plugin-version', 'ac-proto',
                  'ac-remote-wire', 'ac-ui', 'ac-ui-window', 'ac-vfx', 'openac-plugin'
    cargo check --target wasm32-unknown-unknown @($wasmCrates | ForEach-Object { '-p', $_ })
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    cargo check -p ac-runtime --no-default-features --target wasm32-unknown-unknown
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} else {
    Write-Host "wasm32-unknown-unknown not installed; skipping the WebAssembly builds."
}
$datDir = if ($env:OPENAC_DAT_DIR) { $env:OPENAC_DAT_DIR }
          elseif ($env:USERPROFILE) { Join-Path $env:USERPROFILE "Documents\Asheron's Call" }
          else { Join-Path $env:HOME "Documents/Asheron's Call" }
if (Test-Path -LiteralPath (Join-Path $datDir 'client_portal.dat')) {
    Write-Host "Retail dats found in $datDir; running ignored tests too."
    cargo test --workspace -- --include-ignored
} else {
    Write-Host "No retail dats in $datDir; skipping the real-dat tests."
    cargo test --workspace
}
exit $LASTEXITCODE
