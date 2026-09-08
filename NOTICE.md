# Third-Party Notices

This file lists third-party software used by OpenAC, along with their
license terms and copyright notices.

---

## WorldBuilder

Portions of acdream's rendering and dat-handling code are copied from
WorldBuilder (https://github.com/Chorizite/WorldBuilder), MIT-licensed.
The extracted code lives under:

- `src/AcDream.Core/Rendering/Wb/` — pure helpers (texture decode,
  scenery transforms, terrain math).
- `src/AcDream.App/Rendering/Wb/` — renderer infrastructure and mesh pipeline.

Original copyright holders: Chorizite contributors (see WorldBuilder's
LICENSE file). Adapted by the OpenAC maintainers to consume our
`DatCollection` directly (replacing WB's `DefaultDatReaderWriter`) and
to remove editor-only code paths.

Original MIT license text:

MIT License

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

## NuGet dependencies

OpenAC's published payloads redistribute the following packages under their
own licenses. Each license text ships inside the package.

| Package | License |
|---|---|
| Silk.NET (Vulkan, Windowing, Input, OpenAL) | MIT |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | MIT |
| Chorizite.Core, Chorizite.DatReaderWriter | MIT |
| Arch | Apache-2.0 |
| Serilog, Serilog.Sinks.Console | Apache-2.0 |
| SixLabors.ImageSharp | Six Labors Split License (Apache-2.0 terms for open-source use) |
| BCnEncoder.Net, BCnEncoder.Net.ImageSharp | MIT OR Unlicense |
| StbImageSharp, StbTrueTypeSharp | MIT OR Unlicense |
| Microsoft.Extensions.Logging.Abstractions | MIT |
| OpenAL Soft (via Silk.NET.OpenAL.Soft.Native) | LGPL-2.1 (dynamically linked native library; source at https://github.com/kcat/openal-soft) |
| .NET runtime (self-contained payloads) | MIT |

SixLabors.ImageSharp's Split License grants Apache-2.0 terms to open-source
projects; a commercial product built on OpenAC may need its own ImageSharp
license.

## Reference projects

OpenAC's behavior was verified against, but does not include code from,
ACEmulator (AGPL-3.0), ACViewer (GPL-3.0), and holtburger (AGPL-3.0). See the
provenance notes in CONTRIBUTING.md.

## Trademarks and game assets

Asheron's Call and all associated names, art, and data files are the property
of their respective owners. OpenAC distributes no game assets and is not
affiliated with Microsoft, Turbine, or Warner Bros. Entertainment.
