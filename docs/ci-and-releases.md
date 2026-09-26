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
push its matching tag on the branch that release comes from:

```bash
git tag v0.1.0          # on main: the release everyone receives
git push origin v0.1.0

git tag v0.1.1-dev.1    # on dev: a test build, see "Two release trains"
git push origin v0.1.1-dev.1
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
launcher-fingerprint.json the launcher's fingerprint for this release (see below)
AcDream.Plugin.Abstractions.<version>.nupkg   the plugin API package, plus a .sha256 beside it
```

The launcher fingerprint is a SHA-256 that `tools/launcher-fingerprint.ps1`
computes from the committed tree (`-ListInputs` prints every input). It covers
the launcher's own code (`AcDream.Launcher`, `AcDream.Launcher.Core`,
`AcDream.Platform`), the bake tool's own project (`AcDream.Bake`), the
prepared-data recipe (`CurrentFormatVersion` and `CurrentBakeToolVersion` in
`AcDream.Content/Pak/PakFormat.cs`), the build-wide files (`global.json`,
`Directory.Packages.props`, `NuGet.Config`, `Directory.Build.props` without its
version), the launcher icons, the packaging scripts, the .NET SDK the build
resolves and the runtime it bundles, the shared `dotnet publish` flags and the
workflow's calls to `publish-bin.ps1`. It deliberately leaves out the shared
game code (`AcDream.Core`, the rest of `AcDream.Content`): the bake tool an
older launcher carries still prepares valid data until the recipe version goes
up, so a release that only changes game code does not update the launcher.
Raise the recipe version whenever a change alters what the bake tool writes.
`LauncherFingerprintContractTests` pins this rule. The launcher carries the same
value (assembly metadata), so a launcher whose fingerprint matches the
release's does not update itself. It is a separate file because a launcher from
before fingerprints reads `manifest.json` strictly and would refuse a field it
does not know. The macOS launchers are built on their own runners; if those
resolve a different SDK patch, their fingerprint differs and they simply keep
updating by the old rule.

The plugin API package is the one assembly a plugin references, so attaching
it to every release lets a plugin kept in its own repository build against a
released contract instead of against a checkout of the client.

If `macos-intel` also succeeded, `release` downloads its assets too, the
manifest lists them, and a second release step attaches `client-osx-x64.zip`
and `launcher-osx-x64.zip` and appends one line to the release body. That step
has `continue-on-error`, so it never fails the release job. An Intel launcher
that checks for updates between the two steps, or after a failed attach, gets a
failed update check until the job is re-run. A failed or skipped `macos-intel`
leaves the release exactly as it would be without Intel support.

The client payload (the graphical client and the headless host) is published
ahead-of-time compiled as well as self-contained: the code a player reaches
for the first time -- the first object torn down, the first portal, the first
spell -- is already native machine code instead of being compiled inside the
frame that needs it, which is what a first-minute hitch is made of. It costs
about 13 MB of download and a minute of publish time. The launcher and the
bake tool beside it stay just-in-time: they start once, have no frames to
miss, and compiling them ahead of time added about 32 MB to the download for
no measurable gain. The compiler runs on the build host and targets the
payload's runtime, so the Windows release job produces the Linux payload as
well.

`publish-bin.ps1` uses the repository version by default and rejects a supplied
version that does not match it. The tag, package manifest, assembly metadata,
and client label therefore describe the same release. Source builds may append
commit metadata to the assembly informational version; the client label shows
the release number without that suffix.

The manifest's `minimumLauncherVersion` is the release's own version unless
`-MinimumLauncherVersion` names an older one. A launcher refuses to install a
client whose release asks for a newer launcher and updates itself first, which
it does anyway whenever a release carries a newer launcher. Keeping the
minimum at the release is what stops a launcher from before a change in how
the launcher and client share the install from putting the new client in
place first: the single install folder was such a change, and a 0.1.16
launcher that installed a newer client would run it against the old folders.
Name an older minimum only for a release whose client works with every
launcher back to it. The launcher rejects a manifest whose minimum is newer
than the release, and `publish-bin.ps1` refuses to write one.

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

### Two release trains

|  | Stable | Pre-release |
|---|---|---|
| Branch | `main` | `dev` |
| Tag | `vX.Y.Z` | `vX.Y.Z-dev.N` |
| Marked | the latest release | a pre-release, never the latest release |
| Reaches | everyone: every launcher offers it | only a launcher pointed at it by hand |
| Assets | the list above | the same list |
| Kept | indefinitely | the newest five |

The release job decides which train a tag belongs to in one step, **Decide the
release train**, and the publish steps read nothing but its answer. The tag is
the decision: a version carrying a SemVer pre-release part is a test build.
That is what keeps a test build away from players, because the launcher polls
`https://github.com/eriknihlen/OpenAC/releases/latest/download/manifest.json`
and GitHub's `latest` route skips pre-releases. Every step that touches the
release passes the same pair of flags, including the one that attaches the
Intel assets afterwards, because anything else there would reset them on the
release it appends to.

The same step refuses a tag from the wrong branch: a pre-release tag has to
point at a commit reachable from `dev`, and a stable tag at one reachable from
`main`. Tagging the wrong branch fails the job instead of publishing to the
wrong audience.

Versions must sort above the previous release under SemVer 2.0 or the launcher
will not offer the update. SemVer orders `0.1.12 < 0.1.13-dev.1 < 0.1.13`, so a
test build sits above the last release and below the stable release it
rehearses. `Directory.Build.props` still holds the one version for the whole
repository and `publish-bin.ps1` still refuses a tag that disagrees with it, so
a pre-release needs its own version commit on `dev` -- set `0.1.13-dev.1`, then
push `v0.1.13-dev.1` -- exactly as a stable release needs one on `main`.

After a pre-release publishes, the `prune-pre-releases` job runs
`tools/prune-dev-prereleases.ps1 -Keep 5`: every `-dev` pre-release past the
newest five is deleted together with its tag. It ranks by version rather than
by publication date, considers only `-dev` tags that GitHub reports as
pre-releases, and does nothing when there are five or fewer. Rehearse it with
`-WhatIf`, and check its selection rule alone, with no network access, with
`-SelfTest`; a test in the release-train suite runs that check.

To run a pre-release, see "Testing a pre-release" in
[the launcher guide](launcher.md).

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
