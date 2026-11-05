# Third-Party Notices

This file lists the third-party work openac-rust is built from or ships
with, along with its license terms and copyright notices. openac-rust itself
is released under the [MIT License](LICENSE).

---

## OpenAC

openac-rust is a port of [OpenAC](https://github.com/eriknihlen/OpenAC), the
C# Asheron's Call client, to Rust. Its game logic, protocol handling,
rendering rules, user interface, launcher and their tests are translated from
OpenAC's source, which is released under the MIT License with the same
copyright line as this repository's [LICENSE](LICENSE):

Copyright (c) 2026 Erik Nihlén and OpenAC contributors

## DatReaderWriter

The dat-file reader (`crates/ac-dat`) is a Rust port of the file formats as
read by DatReaderWriter (https://github.com/Chorizite/DatReaderWriter),
MIT-licensed.

Copyright 2024 ACClientLib

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## WorldBuilder

Parts of OpenAC's terrain, scenery and texture code were adapted from
WorldBuilder (https://github.com/Chorizite/WorldBuilder), MIT-licensed. Where
openac-rust ports those parts of OpenAC, WorldBuilder's notice applies too.

Copyright (c) Chorizite contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Texture decoders

OpenAC decodes block-compressed (DXT) textures with BCnEncoder.Net
(https://github.com/Nominom/BCnEncoder.NET, MIT OR Unlicense) and JPEG
textures with StbImageSharp (https://github.com/StbSharp/StbImageSharp, a C#
port of stb_image; MIT OR Unlicense / public domain). openac-rust has its own
decoders (`crates/ac-dat/src/texture/`), written to produce the same pixels;
their golden test values were taken from those libraries. Both are used here
under their permissive terms (MIT).

## Noto Sans

The game client embeds unmodified Noto Sans Regular and SemiBold from the Noto
project (compiled into `crates/ac-plugin-ui`). Copyright 2018 The Noto Project Authors.
Licensed under the SIL Open Font License 1.1. See
[the full license](assets/fonts/NotoSans/OFL.txt) and
[the pinned source and checksum](assets/fonts/NotoSans/README.md). Release
packages include a copy as `licenses/NotoSans-OFL.txt`.

## Cantarell

On Linux, the Wayland window decorations (`sctk-adwaita`, through winit)
embed unmodified Cantarell Regular 0.0.5 for the title bar's text when the
desktop's own font cannot be found; it is compiled into the Linux `openac`
and `openac-launcher` executables only. Copyright (c) 2009-2011, Understanding Limited; Copyright (c)
2010-2011, Jakub Steiner. Licensed under the SIL Open Font License 1.1. See
[the full license](assets/fonts/Cantarell/OFL.txt) and
[its source](assets/fonts/Cantarell/README.md). Release packages include a
copy as `licenses/Cantarell-OFL.txt`.

## Launcher fonts (egui)

The launcher window is drawn with [egui](https://github.com/emilk/egui), whose
`default_fonts` feature compiles four fonts into the launcher through the
`epaint_default_fonts` crate (license expression
`(MIT OR Apache-2.0) AND OFL-1.1 AND Ubuntu-font-1.0`):

| Font | License |
|---|---|
| Hack Regular | MIT (Copyright 2018 Source Foundry Authors) and the Bitstream Vera License (Copyright 2003 Bitstream, Inc.) |
| Ubuntu Light | Ubuntu Font Licence 1.0 |
| Noto Emoji Regular | SIL Open Font License 1.1 |
| emoji-icon-font | MIT (Copyright 2014 John Slegers) |

The license texts ship in the `epaint_default_fonts` crate's `fonts/` folder
and are included in release packages under `licenses/egui-fonts/`.

## Rust crates

The programs link the crates listed in `Cargo.lock`, each under its own
license, unmodified from crates.io. Each crate's license text ships in its
source package, and every release package includes
`THIRD-PARTY-LICENSES.html`, which lists each crate compiled into its
programs with its license text (generated by
[cargo-about](https://github.com/EmbarkStudios/cargo-about) from `about.toml`).
Run `cargo metadata --format-version 1` for the exact list. As
of this writing, the crates compiled into `openac` and `openac-launcher` on
Windows and Linux are under these licenses:

| License | Examples |
|---|---|
| MIT OR Apache-2.0 (and the same with Zlib, BSD-2-Clause or 0BSD as further options) | wgpu, glam, egui, arboard, bytemuck, miniz_oxide, sha2, native-tls, zerocopy, most others |
| MIT | tracing, fancy-regex, generic-array, schannel, nix, openssl-sys, the Wayland client crates |
| Apache-2.0 | winit, cpal, openssl (the Rust bindings), ab_glyph (Linux window decorations' title text) |
| Apache-2.0 WITH LLVM-exception | wasmtime, cranelift, regalloc2, pulley-interpreter |
| Apache-2.0 WITH LLVM-exception OR Apache-2.0 OR MIT | wasmparser, wasm-encoder, rustix, linux-raw-sys |
| MIT OR Unlicense | memchr, aho-corasick |
| MIT OR Apache-2.0 OR Zlib | fontdue, raw-window-handle |
| MPL-2.0 | symphonia-core, symphonia-bundle-mp3 |
| BSL-1.0 | clipboard-win, error-code (Windows) |
| BSD-3-Clause | tiny-skia (Linux window decorations) |
| ISC | libloading |
| Zlib | foldhash |

Notes:

- **MPL-2.0.** The symphonia crates decode the one MP3 sound in the game's
  data. They are used unmodified; their source is available from crates.io
  and https://github.com/pdeljanov/Symphonia.
- **Dual-licensed crates taken under the permissive option.** `self_cell`
  (`Apache-2.0 OR GPL-2.0-only`, through egui) is used under Apache-2.0.
  `r-efi` (`MIT OR Apache-2.0 OR LGPL-2.1-or-later`), which appears in
  `Cargo.lock` for UEFI targets only and is not part of the Windows or Linux
  programs, would be used under MIT. Every other `OR` expression is taken
  under its MIT or Apache-2.0 option.
- **C code.** wasmtime compiles two small C files of its own (Apache-2.0
  WITH LLVM-exception) into the program.

## System libraries

openac-rust links the operating system's libraries and does not ship them:
the graphics drivers (Direct3D 12 or Vulkan), the audio stack (WASAPI on
Windows, ALSA on Linux), and the TLS stack used by the launcher and by
plugins' network access: SChannel on Windows and the system OpenSSL on Linux
(Apache-2.0), linked dynamically.

## Reference projects

openac-rust's behavior was checked against, but does not include code from,
ACEmulator (AGPL-3.0), ACViewer (GPL-3.0), and holtburger (AGPL-3.0). See
[CONTRIBUTING.md](CONTRIBUTING.md).

## Trademarks and game assets

Asheron's Call and all associated names, art, and data files are the property
of their respective owners. openac-rust distributes no game assets and is not
affiliated with Microsoft, Turbine, or Warner Bros. Entertainment.
