# Continuous integration and releases

Workflow: [`.github/workflows/ci.yml`](https://github.com/eriknihlen/OpenAC/blob/main/.github/workflows/ci.yml).

## Triggers and runners

| Event | `windows-gate` | `linux-portable` | `macos-portable` | `vulkan-hardware` | `release` |
|---|---|---|---|---|---|
| Pull request | GitHub-hosted `windows-latest` | GitHub-hosted `ubuntu-latest` | GitHub-hosted Apple-silicon `macos-14` | not run | not run |
| Push to `main` | self-hosted `openac-windows` | self-hosted `openac-linux` | GitHub-hosted Apple-silicon `macos-14` | self-hosted `openac-windows` (NVIDIA GPU) | not run |
| Push of a `v*` tag | self-hosted | self-hosted | GitHub-hosted Apple-silicon | self-hosted | self-hosted, after all four are green |

`macos-intel` is a separate job, not part of `macos-portable`, that builds and
tests Intel (`osx-x64`) on GitHub-hosted `macos-15-intel` for every trigger
above. It is best effort until August 2027 (see "Retiring Intel macOS
support" below) and is not one of the four gates `release` waits on.

Changes that touch only Markdown files, `docs/`, `LICENSE`, or the issue
templates skip the workflow entirely (`paths-ignore` on both triggers); a
typo fix does not need a full test run. Tag pushes ignore path filters, so a
release always runs every gate. If the gate jobs are ever made required
status checks, docs-only pull requests would wait on checks that never
report; GitHub's answer for that case is a second workflow with the inverse
`paths` filter that reports the same job names as passed.

Pull requests from forks run only on GitHub-hosted runners, so untrusted code
never executes on a maintainer's machine. The repository requires approval
before a workflow runs for an outside contributor. Pushes to `main` and tags
run on the maintainer's own machines, which is what lets the Vulkan lane run
on real hardware.

## What the gate runs

`windows-gate` builds `AcDream.slnx` in Release and runs every test project
with the portable filter from `tools/run-release-gate.ps1`. `linux-portable`
runs the presentation-free closure with the Linux lane enabled; the network
test assembly runs single-threaded there because its socket tests contend on a
small container. `macos-portable` enables the macOS lane, publishes the native
Apple-silicon payloads, and checks the Finder-launchable app bundle. GitHub
documents `macos-14` as an Apple-silicon hosted runner. `vulkan-hardware` runs
`Lane=Vulkan` on the NVIDIA runner. A test in
`GitHubWorkflowFilterContractTests` pins every workflow filter to the script's
default so the platform lanes cannot drift.
`macos-intel` mirrors `macos-portable`'s steps for `osx-x64` on
`macos-15-intel`, GitHub's last x86_64 hosted image; contract tests in the
same class pin its filter and check that its success is not required for
`release`.

The two `workflow_dispatch` workflows, `headless-portability.yml` and
`release-gate.yml`, are manual deep checks: the portable closure on both
operating systems including a lavapipe software-Vulkan pass, and the complete
bounded local gate on a hosted Windows runner.

## macOS Vulkan runtime

Each macOS client carries its own Vulkan loader and MoltenVK, so players never
install Vulkan. Apple silicon (`osx-arm64`) bundles the pinned Homebrew
`molten-vk` and `vulkan-loader` formulae installed on the runner, as described
above.

Intel (`osx-x64`) cannot use Homebrew, which no longer builds Intel bottles,
or the LunarG SDK, whose installer runs only on Apple silicon.
`tools/build-macos-x64-vulkan.ps1` instead downloads the pinned MoltenVK
release archive from KhronosGroup/MoltenVK (SHA-256 checked) and builds the
Vulkan loader for x86_64 from its pinned KhronosGroup/Vulkan-Loader SDK tag
(commit checked). It needs CMake, git, and Python 3, which the hosted runner
provides. `macos-intel` caches the output under `artifacts/macos-x64-vulkan`,
keyed on the script, so the loader is rebuilt only when a pin changes.
`tools/package-macos-x64-vulkan.ps1` then bundles it the same way
`tools/package-macos-vulkan.ps1` bundles the Apple-silicon libraries: it
rewrites library identities to `@loader_path`, rejects any library without a
slice for the target architecture, ad-hoc signs, writes the MoltenVK ICD
manifest, and records where each library came from in
`Resources/vulkan/dependencies.json`. It refuses to run against a client that
is not itself Intel.

To move Intel to a newer MoltenVK or loader, edit the pins at the top of
`tools/build-macos-x64-vulkan.ps1`: the MoltenVK release URL and its SHA-256,
and the loader tag and the commit that tag resolves to.

## Releases

The repository version is defined once in `Directory.Build.props`. It supplies
the client, launcher, and other assemblies, including the version shown on the
character-selection screen. Set that version before building a release, then
push its matching tag on `main`:

```bash
git tag v0.1.0
git push origin v0.1.0
```

When the four gate jobs are green, the `release` job downloads the verified
Apple-silicon assets from `macos-portable`, then runs `tools/publish-bin.ps1`
for the Windows and Linux payloads. It writes one manifest whose asset URLs
point at the release for that tag, then creates the GitHub Release with:

```
client-win-x64.zip        AcDream.App.exe + acdream-headless.exe
launcher-win-x64.zip      acdream-launcher.exe + acdream-bake.exe
client-linux-x64.zip      AcDream.App + acdream-headless
launcher-linux-x64.zip    acdream-launcher + acdream-bake
client-osx-arm64.zip      acdream-client + acdream-headless for Apple silicon
launcher-osx-arm64.zip    OpenAC.app Finder bundle with launcher + bake
manifest.json             version, minimum launcher version, asset URLs, SHA-256s
```

If `macos-intel` also succeeded, `release` downloads its assets too, the
manifest lists them, and a second release step attaches `client-osx-x64.zip`
and `launcher-osx-x64.zip` and appends one line to the release body. That step
has `continue-on-error`, so it never fails the release job. An Intel launcher
that checks for updates between the two steps, or after a failed attach, gets a
failed update check until the job is re-run. A failed or skipped `macos-intel`
leaves the release exactly as it would be without Intel support.

`publish-bin.ps1` uses the repository version by default and rejects a supplied
version that does not match it. The tag, package manifest, assembly metadata,
and client label therefore describe the same release. Source builds may append
commit metadata to the assembly informational version; the client label shows
the release number without that suffix.

The Linux and macOS client zips carry Unix file modes, so the executables
extract with the execute bit set; the launcher's own extractor applies them
too. `launcher-osx-arm64.zip` has one top-level item, `OpenAC.app`. Expand it,
move it into `~/Applications`, and open it in Finder. The app's `Info.plist`,
icon, and privacy manifest are generated by `tools/package-macos-launcher.ps1`.
The bare Apple-silicon client executable is `acdream-client`; its Vulkan
loader, MoltenVK ICD, and privacy manifest are generated by
`tools/package-macos-vulkan.ps1`. CI applies and verifies local ad-hoc
signatures until a Developer ID signing and notarization release step is
configured.

`launcher-osx-x64.zip` and the bare Intel client executable follow the same
layout, generated by `tools/package-macos-launcher.ps1` and
`tools/package-macos-x64-vulkan.ps1` respectively. To build the macOS
payloads locally, run `tools/publish-bin.ps1 -MacOnly` on a Mac, with
`-MacRid osx-arm64` (the default, needs Homebrew's `molten-vk` and
`vulkan-loader`) or `-MacRid osx-x64` (builds or reuses the Intel Vulkan
runtime described above; `-MacVulkanRuntimeDirectory` overrides where).
`-MacArtifactsDirectory` accepts a complete `client-<rid>.zip` and
`launcher-<rid>.zip` pair for `osx-arm64`, plus an optional matching pair for
`osx-x64`.

Releases are never flagged pre-release. The launcher polls
`https://github.com/eriknihlen/OpenAC/releases/latest/download/manifest.json`,
and GitHub's `latest` route skips pre-releases; the beta state is carried by
the version string. Versions must sort above the previous release under SemVer
2.0 or the launcher will not offer the update.

To verify a release end to end from a checkout:

```bash
dotnet test tests/AcDream.Launcher.Core.Tests --filter Lane=Live
```

That installs the advertised client from the real feed through the production
updater, with real hash verification and atomic activation, into a temporary
directory.

## Retiring Intel macOS support

GitHub has said `macos-15-intel` is its last x86_64 image, available until
August 2027. Intel support is a separate, non-blocking job today for exactly
this reason: removing it touches only Intel-specific pieces, and the
Apple-silicon lane, launcher, and client code stay as they are. Every
Intel-only line in a file this section does not name outright carries a
comment containing `osx-x64`; `git grep osx-x64` finds all of them.
`tools/build-macos-x64-vulkan.ps1` pins the loader's macOS deployment target
to `LSMinimumSystemVersion` in `tools/package-macos-launcher.ps1`; raise both
together.

Whole files to delete:

1. `tools/build-macos-x64-vulkan.ps1`
2. `tools/package-macos-x64-vulkan.ps1`
3. Every `src/**/packages.osx-x64.lock.json`

Blocks to delete:

1. In `.github/workflows/ci.yml`: the `macos-intel` job, and the `osx-x64`
   download and release steps in `release`.
2. In `tools/publish-bin.ps1`: the `-MacRid` and `-MacVulkanRuntimeDirectory`
   parameters, and every block marked `# osx-x64:` (the `-MacRid` check and
   RID swap, the client staging block, the client executables override, the
   launcher bundle block, and the release artifact pair). No upstream line in
   this file was changed.
3. In `GitHubWorkflowFilterContractTests`: the `osx-x64` filter assertion and
   `ReleaseJob_DoesNotHardGateOnMacosIntel`.
4. The `osx-x64` test cases in
   `tests/AcDream.Launcher.Core.Tests/Updates/LauncherRuntimeIdentityTests.cs`
   and `PayloadExecutableNamesTests.cs`.
5. This section, the "macOS Vulkan runtime" Intel paragraphs above, and the
   `osx-x64` sentences added to `docs/building-and-running.md`.

Shared lines to edit back to upstream:

1. The `release` job's `if:` reverts to `if: startsWith(github.ref,
   'refs/tags/v')`, and its comment reverts to the original two lines about
   `needs` guaranteeing a red gate cannot publish.
2. The `release` job's `needs:` list drops `macos-intel`.

Launchers already installed on Intel Macs keep the client they have. A
release without `osx-x64` payloads fails their update check, which the
launcher shows as "Updates could not be checked" rather than installing
anything.

## Self-hosted runner notes

- The Windows runner must run in an interactive session (a scheduled task at
  logon, not a service) so the Vulkan lane can open a device.
- Each runner needs the .NET SDK band from `global.json`, Git, and on Windows
  PowerShell 7.
- Runners poll GitHub outbound over HTTPS; no inbound ports are required.
