# tools/

| Path | Purpose |
|---|---|
| `ShaderCompiler/` | .NET shader compiler over Silk.NET.Shaderc used by `compile-shaders.ps1`. Part of `AcDream.slnx`. |
| `RenderPackValidator/` | Validates a render pack against the SDK contract. Part of `AcDream.slnx`; `tests/AcDream.RenderPackValidator.Tests` covers it. |
| `compile-shaders.ps1` | Recompiles every GLSL source to the committed SPIR-V and rewrites the freshness manifest. |
| `build-linux.sh` | Linux-friendly Release build. Keeps .NET, NuGet, MSBuild, and generated lock state outside the checkout; pass `--test` for the portable test filter. |
| `run-release-gate.ps1` | The complete bounded local test gate: locked restore, Release build, every test assembly in its own timed process, TRX and hash evidence under `artifacts/release-gate/`. Its default `-TestFilter` is the portable filter CI copies. |
| `publish-bin.ps1` | Builds the self-contained client and launcher payloads, `manifest.json` and `launcher-fingerprint.json`. CI runs it in the release job; locally it is for inspection. |
| `run-launcher-trial.ps1` | Builds this branch's client and launcher and runs them together, isolated in `artifacts/launcher-trial/` from the tester's real OpenAC install. |
| `plugin-extraction-dryrun.ps1` | Rehearses moving a plugin to its own repository, in scratch copies: builds and tests the client with the plugin deleted, and builds and tests the plugin on its own against the packed contract. See [extracting a plugin](../docs/plugins/extracting-a-plugin.md). |
| `update-package-locks.ps1` | Regenerates every neutral and RID NuGet lock file after a dependency change. |
