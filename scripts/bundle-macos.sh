#!/usr/bin/env sh
# Build OpenAC.app on macOS: the launcher and the client in one ad-hoc signed bundle, and its
# zip. The launcher must run from OpenAC.app/Contents/MacOS (its self-update replaces the
# bundle), and it starts the `openac` beside it until a client is installed through its update
# feed. Writes target/<target>/release/macos/OpenAC.app and OpenAC-<version>-<rid>.zip there.
#
#   scripts/bundle-macos.sh [--target aarch64-apple-darwin | x86_64-apple-darwin]
set -eu
cd "$(dirname "$0")/.."

if [ "$(uname -s)" != Darwin ]; then
    echo "bundle-macos.sh builds a macOS app bundle and must run on macOS." >&2
    exit 1
fi

usage() {
    echo "usage: scripts/bundle-macos.sh [--target <triple>]" >&2
    exit 2
}
target=""
case "${1:-}" in
    --target)
        target="${2:?--target needs a target triple}"
        [ "$#" -eq 2 ] || usage
        ;;
    "") [ "$#" -eq 0 ] || usage ;;
    *) usage ;;
esac

# The license texts every release zip carries; cargo-about lists each crate compiled in.
if ! cargo about --version > /dev/null 2>&1; then
    echo "cargo-about is needed for the license list: cargo install --locked cargo-about --version 0.9.2 --features cli" >&2
    exit 1
fi

# Cargo's build folder, which CARGO_TARGET_DIR moves.
target_dir="${CARGO_TARGET_DIR:-target}"
if [ -n "$target" ]; then
    cargo build --release --locked --target "$target" -p openac -p openac-launcher
    bin="$target_dir/$target/release"
    arch="${target%%-*}"
else
    cargo build --release --locked -p openac -p openac-launcher
    bin="$target_dir/release"
    arch="$(uname -m)"
fi
case "$arch" in
    aarch64 | arm64) rid=osx-arm64 ;;
    x86_64) rid=osx-x64 ;;
    *) echo "No macOS runtime identifier for $arch." >&2; exit 1 ;;
esac

# The workspace version (the first `version` in Cargo.toml is [workspace.package]'s).
version="$(sed -n 's/^version = "\(.*\)"$/\1/p' Cargo.toml | head -n 1)"
[ -n "$version" ] || { echo "No workspace version in Cargo.toml." >&2; exit 1; }

out="$bin/macos"
bundle="$out/OpenAC.app"
zip="$out/OpenAC-$version-$rid.zip"
rm -rf "$bundle" "$zip" "$out/OpenAC.iconset"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"

# Contents/MacOS holds only code: codesign refuses a bundle with a plain file there.
install -m 0755 "$bin/openac-launcher" "$bin/openac" "$bundle/Contents/MacOS/"
cp LICENSE NOTICE.md "$bundle/Contents/Resources/"

# The same license files as the other release zips: every crate compiled in, the bundled
# Noto Sans font, and the launcher's fonts (their texts ship in the font crate).
resources="$bundle/Contents/Resources"
cargo about generate --workspace --locked --fail \
    -c about.toml -o "$resources/THIRD-PARTY-LICENSES.html" about.hbs
[ -s "$resources/THIRD-PARTY-LICENSES.html" ] || { echo "The license list is empty." >&2; exit 1; }
mkdir -p "$resources/licenses/egui-fonts"
cp assets/fonts/NotoSans/OFL.txt "$resources/licenses/NotoSans-OFL.txt"
fonts_version="$(awk '/^name = "epaint_default_fonts"$/ { getline; print; exit }' Cargo.lock \
    | sed -n 's/^version = "\(.*\)"$/\1/p')"
fonts_dir="$(find "${CARGO_HOME:-$HOME/.cargo}/registry/src" -maxdepth 2 -type d \
    -name "epaint_default_fonts-$fonts_version" 2> /dev/null | head -n 1)/fonts"
for file in OFL.txt UFL.txt Hack-Regular.txt emoji-icon-font-mit-license.txt; do
    cp "$fonts_dir/$file" "$resources/licenses/egui-fonts/"
done

# The icon, from the launcher's 256-pixel art (no larger source exists, so no larger entries).
iconset="$out/OpenAC.iconset"
mkdir -p "$iconset"
for entry in 16:icon_16x16 32:icon_16x16@2x 32:icon_32x32 64:icon_32x32@2x \
    128:icon_128x128 256:icon_128x128@2x 256:icon_256x256; do
    sips -z "${entry%%:*}" "${entry%%:*}" assets/icons/launcher-256.png \
        --out "$iconset/${entry#*:}.png" > /dev/null
done
iconutil --convert icns --output "$bundle/Contents/Resources/OpenAC.icns" "$iconset"
rm -rf "$iconset"

# org.openac.rust: its own preferences domain, apart from OpenAC's (org.openac.*) bundles.
cat > "$bundle/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key>
  <string>OpenAC</string>
  <key>CFBundleExecutable</key>
  <string>openac-launcher</string>
  <key>CFBundleIconFile</key>
  <string>OpenAC.icns</string>
  <key>CFBundleIdentifier</key>
  <string>org.openac.rust</string>
  <key>CFBundleName</key>
  <string>OpenAC</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$version</string>
  <key>CFBundleVersion</key>
  <string>$version</string>
  <key>LSMinimumSystemVersion</key>
  <string>14.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
EOF
plutil -lint "$bundle/Contents/Info.plist" > /dev/null

# Ad-hoc signatures (no Apple Developer certificate): each program, then the bundle, which seals
# Info.plist and the resources. Apple silicon runs no unsigned code.
for program in openac openac-launcher; do
    codesign --force --sign - --timestamp=none "$bundle/Contents/MacOS/$program"
done
codesign --force --sign - --timestamp=none "$bundle"
codesign --verify --deep --strict "$bundle"

"$bundle/Contents/MacOS/openac-launcher" --verify-publish
"$bundle/Contents/MacOS/openac" --help > /dev/null

# No resource forks or extended attributes (macOS stamps com.apple.provenance on every file):
# the signatures live in the programs and _CodeSignature.
ditto -c -k --norsrc --noextattr --keepParent "$bundle" "$zip"

echo "Created $bundle"
echo "Created $zip"
