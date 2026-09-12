# Building and running

## Prerequisites

- **.NET 10 SDK** in the band pinned by `global.json` (currently `10.0.3xx`).
- **Your own Asheron's Call data files**: `client_portal.dat`,
  `client_cell_1.dat`, `client_highres.dat`, `client_local_English.dat`.
  OpenAC does not distribute them.
- **A server.** OpenAC connects to ACEmulator. The examples use a local server
  at `127.0.0.1:9000`.
- For the graphical client, a **Vulkan 1.3** capable GPU and driver. On
  Linux that means your distribution's Vulkan ICD for your GPU (for example
  `mesa-vulkan-drivers` on Ubuntu) and an X11 or Wayland desktop. On macOS it
  means MoltenVK and the Vulkan loader (`brew install molten-vk
  vulkan-loader`) when running from source; MoltenVK is reached through
  `VK_KHR_portability_enumeration`, and the loader needs
  `VK_ICD_FILENAMES=$(brew --prefix)/etc/vulkan/icd.d/MoltenVK_icd.json`.
  The Apple-silicon launcher distribution supplies its own validated loader,
  MoltenVK, and ICD manifest for launched clients.

Windows and Linux (x64), plus macOS (arm64), are supported by the launcher,
graphical client, and bake step. The examples below use PowerShell; the bash
equivalents differ only in how variables are set.

## Build and test

```bash
dotnet restore AcDream.slnx
dotnet build AcDream.slnx -c Release
dotnet test AcDream.slnx -c Release --no-build --filter "Lane!=InstalledDat&Lane!=PreparedPackage&Lane!=Live&Lane!=Manual&Lane!=Timing&Lane!=Windows&Lane!=Linux&Lane!=MacOS&Lane!=Unix&Lane!=Vulkan&Lane!=SystemFont&Purpose!=Diagnostic&Status!=KnownFailure"
```

The filter is the portable gate described in `release-gate.md`. Tests behind a
`Lane` need a specific resource; run them when you have it, for example
`--filter "Lane=Vulkan"` on a machine with a GPU.

On Linux, `bash tools/build-linux.sh` runs the Release build with .NET and
NuGet state isolated under
`XDG_CACHE_HOME` (or `/tmp`). This is useful in containers and CI workers with
a read-only home directory. It redirects generated package locks to that cache
so local restores do not rewrite tracked package-lock files. Pass `--test` to
run the portable test filter afterward.

## Prepare the content package

Rendering and collision read a validated prepared package instead of decoding
world meshes on the frame path. Build it once per machine:

```powershell
dotnet run --project src/AcDream.Bake/AcDream.Bake.csproj -c Release -- `
  --dat-dir "C:\Games\Asheron's Call" `
  --out "C:\Games\Asheron's Call\acdream.pak"
```

A complete package from the standard data set is about 570 MiB. It is
machine-local; do not commit it. `ACDREAM_PAK_PATH` overrides the default
`<DAT directory>/acdream.pak`. The launcher runs this step for you.

## Run the graphical client

```powershell
$env:ACDREAM_DAT_DIR   = "C:\Games\Asheron's Call"
$env:ACDREAM_PAK_PATH  = "C:\Games\Asheron's Call\acdream.pak"
$env:ACDREAM_LIVE      = "1"
$env:ACDREAM_TEST_HOST = "127.0.0.1"
$env:ACDREAM_TEST_PORT = "9000"
$env:ACDREAM_TEST_USER = "youraccount"
$env:ACDREAM_TEST_PASS = "yourpassword"

dotnet run --project src/AcDream.App/AcDream.App.csproj -c Release
```

On Linux:

```bash
export ACDREAM_DAT_DIR="$HOME/ac" ACDREAM_PAK_PATH="$HOME/ac/acdream.pak"
export ACDREAM_LIVE=1 ACDREAM_TEST_HOST=127.0.0.1 ACDREAM_TEST_PORT=9000
export ACDREAM_TEST_USER=youraccount ACDREAM_TEST_PASS=yourpassword
dotnet run --project src/AcDream.App/AcDream.App.csproj -c Release
```

The DAT directory can instead be the first positional argument.

For a macOS source build, set the same game variables and run the managed
assembly with the Homebrew loader available:

```bash
export DYLD_LIBRARY_PATH="$(brew --prefix)/lib"
export VK_DRIVER_FILES="$(brew --prefix)/etc/vulkan/icd.d/MoltenVK_icd.json"
dotnet src/AcDream.App/bin/Release/net10.0/AcDream.App.dll "$HOME/ac"
```

The packaged Mac client is named `acdream-client` and loads its bundled graphics
libraries directly. It does not require those environment variables.

## Useful startup options

| Variable | Effect |
|---|---|
| `ACDREAM_DAT_DIR` | Data-file directory |
| `ACDREAM_PAK_PATH` | Prepared package path; defaults to `<DAT dir>/acdream.pak` |
| `ACDREAM_LIVE=1` | Connect to a server instead of loading offline |
| `ACDREAM_TEST_HOST` / `ACDREAM_TEST_PORT` | Server endpoint |
| `ACDREAM_TEST_USER` / `ACDREAM_TEST_PASS` | Graphical-client credentials |
| `ACDREAM_NO_AUDIO=1` | Skip audio initialization |
| `ACDREAM_UNCAPPED_RENDER=1` | Disable frame pacing (for measurement only) |
| `ACDREAM_DISPLAY_PROTOCOL=auto\|x11\|wayland` | Linux window backend selection |
| `ACDREAM_DEVTOOLS=1` | Enable the Vulkan validation and debug-utils layers |

A few other `ACDREAM_*` variables switch original-client behaviors that are
on by default (`ACDREAM_RETAIL_CHASE`, `ACDREAM_CAMERA_COLLIDE`,
`ACDREAM_CAMERA_ALIGN_SLOPE`, `ACDREAM_RETAIL_CLOSE_DEGRADES`; set `=0` to
disable one for comparison). The rest are diagnostic probes, off by
default and documented beside their read sites in `src/`.

## Run a headless session

`AcDream.Headless` loads no window, GPU, or audio assembly. Create `bot.json`:

```json
{
  "version": 1,
  "process": {
    "content": {
      "datDirectory": "/opt/ac",
      "preparedAssetPath": "/opt/ac/acdream.pak"
    }
  },
  "sessions": [
    {
      "id": "bot-1",
      "endpoint": { "host": "127.0.0.1", "port": 9000 },
      "account": "youraccount",
      "character": { "index": 0 },
      "policy": { "id": "idle" },
      "credential": {
        "provider": "environment",
        "reference": "ACDREAM_BOT_PASSWORD"
      }
    }
  ]
}
```

Then:

```bash
export ACDREAM_BOT_PASSWORD='yourpassword'
dotnet run --project src/AcDream.Headless/AcDream.Headless.csproj -c Release -- validate --config bot.json
dotnet run --project src/AcDream.Headless/AcDream.Headless.csproj -c Release -- run --config bot.json
```

For a single local session, `run` also accepts `--user` and `--password`. Add
more session entries for a multi-session process. Built-in policies:
`idle`, `lifecycle-smoke`, `observer-movement`, `portal-route-smoke`.

## Run the launcher from source

```bash
dotnet run --project src/AcDream.Launcher/AcDream.Launcher.csproj -c Release
```

The launcher installs released builds from the GitHub Releases feed. A
source-built launcher will offer to update itself to the latest release; that
is expected.

## Shaders

GLSL sources live in `src/AcDream.App/Rendering/Shaders/`; the committed
SPIR-V beside them is what the client loads. After editing a shader run
`tools/compile-shaders.ps1`, which uses `glslc` from a Vulkan SDK if present
and otherwise the bundled `tools/ShaderCompiler`.
