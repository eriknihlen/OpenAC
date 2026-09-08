# Continuous integration and releases

Workflow: [`.github/workflows/ci.yml`](../.github/workflows/ci.yml).

## Triggers and runners

| Event | `windows-gate` | `linux-portable` | `vulkan-hardware` | `release` |
|---|---|---|---|---|
| Pull request | GitHub-hosted `windows-latest` | GitHub-hosted `ubuntu-latest` | not run | not run |
| Push to `main` | self-hosted `openac-windows` | self-hosted `openac-linux` | self-hosted `openac-windows` (NVIDIA GPU) | not run |
| Push of a `v*` tag | self-hosted | self-hosted | self-hosted | self-hosted, after all three are green |

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
small container. `vulkan-hardware` runs `Lane=Vulkan` on the NVIDIA runner. A
test in `GitHubWorkflowFilterContractTests` pins the workflow's filter strings
to the script's default so the two cannot drift.

The two `workflow_dispatch` workflows, `headless-portability.yml` and
`release-gate.yml`, are manual deep checks: the portable closure on both
operating systems including a lavapipe software-Vulkan pass, and the complete
bounded local gate on a hosted Windows runner.

## Releases

Push a tag of the form `vX.Y.Z` or `vX.Y.Z-beta.N` on `main`:

```bash
git tag v0.1.0-beta.1
git push origin v0.1.0-beta.1
```

When the three gate jobs are green, the `release` job runs
`tools/publish-bin.ps1`, which publishes self-contained `win-x64` payloads and
writes a manifest whose asset URLs point at the release for that tag, then
creates the GitHub Release with:

```
client-win-x64.zip      AcDream.App.exe + acdream-headless.exe
launcher-win-x64.zip    acdream-launcher.exe + acdream-bake.exe
manifest.json           version, minimum launcher version, asset URLs, SHA-256s
```

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

## Self-hosted runner notes

- The Windows runner must run in an interactive session (a scheduled task at
  logon, not a service) so the Vulkan lane can open a device.
- Each runner needs the .NET SDK band from `global.json`, Git, and on Windows
  PowerShell 7.
- Runners poll GitHub outbound over HTTPS; no inbound ports are required.
