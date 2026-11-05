# Cantarell Regular (licence only)

The font itself is not in this folder. On Linux, winit's Wayland window
decorations (`sctk-adwaita`, through winit's `wayland-csd-adwaita` feature)
draw the title bar's text and fall back to the Cantarell Regular they compile
in (`include_bytes!` of `src/title/Cantarell-Regular.ttf` in `sctk-adwaita`
0.10.1, SHA-256
d15f25bcdaba2e25f54c32d4e29e68fedeb1b7a1d8fbc83f7c4475916e93b55f) when the
desktop's own font cannot be found. That file is Cantarell 0.0.5, under SIL
OFL 1.1, so it ships inside the Linux `openac` and `openac-launcher`
executables; the Windows and macOS builds do not include it.

Licence: `OFL.txt` beside this file, the `COPYING` of the cantarell-fonts
release that matches the bundled font (its copyright lines are the font's
own), with line endings normalised to LF.

Source: https://gitlab.gnome.org/GNOME/cantarell-fonts/-/raw/0.0.5/COPYING
(tag 0.0.5, commit 61362600e104b96c88a2fdc068e1e2c68aef4f16; SHA-256 of the
file as published, CRLF line endings included:
7bb92a19f8f5485713f6e96a8331289b3aae49e8e44e7cad58b567e03be16d76).

A release ships `OFL.txt` as `licenses/Cantarell-OFL.txt`.
