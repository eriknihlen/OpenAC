# tools/

| Path | Purpose |
|---|---|
| `ShaderCompiler/` | .NET shader compiler over Silk.NET.Shaderc used by `compile-shaders.ps1` when no Vulkan SDK `glslc` is on the machine. Part of `AcDream.slnx`. |
| `RenderPackValidator/` | Validates a render pack against the SDK contract. Part of `AcDream.slnx`; `tests/AcDream.RenderPackValidator.Tests` covers it. |
| `compile-shaders.ps1` | Recompiles every GLSL source to the committed SPIR-V and rewrites the freshness manifest. |
| `run-release-gate.ps1` | The complete bounded local test gate: locked restore, Release build, every test assembly in its own timed process, TRX and hash evidence under `artifacts/release-gate/`. Its default `-TestFilter` is the portable filter CI copies. |
| `publish-bin.ps1` | Builds the self-contained client and launcher payloads and `manifest.json`. CI runs it in the release job; locally it is for inspection. |
| `update-package-locks.ps1` | Regenerates every neutral and RID NuGet lock file after a dependency change. |
