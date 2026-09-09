# Complete Release gate

The default release gate is repository-owned and uses the SDK feature band in
`global.json`:

```powershell
pwsh ./tools/run-release-gate.ps1
```

The command verifies that `AcDream.slnx` contains every `.csproj` under `src/`,
`tests/`, and `tools/`, performs a locked restore, builds that complete graph,
then discovers and runs every hermetic test in every default test assembly once
in a fresh Release process. It does not retry failures. Tests carrying an
explicit non-hermetic `Lane` trait (`InstalledDat`, `PreparedPackage`, `Live`,
`Manual`, `Timing`, `Windows`, `Linux`, `Vulkan`, or `SystemFont`),
`Purpose=Diagnostic`, or `Status=KnownFailure` are excluded from the hermetic
total and run through their owned lane instead. The graph currently contains
40 projects, including the shader compiler, the render-pack validator, and
three render-pack SDK samples; tools and SDK samples are built but are not
executed as tests.

Build and dependency policy is repository-owned:

- `global.json` pins the accepted .NET SDK feature band;
- `Directory.Build.props` supplies the common target framework, language,
  nullable, analyzer, warnings-as-errors, deterministic-build, and lock-file
  settings;
- `Directory.Packages.props` is the only direct package-version table;
- `NuGet.Config` clears machine sources and permits only `nuget.org`; and
- each supported project commits its own `packages.neutral.lock.json`; shipped
  source projects also commit `packages.win-x64.lock.json` and
  `packages.linux-x64.lock.json` for RID-specific publishes.

The nonstandard neutral name is intentional. NuGet always prefers a
conventional `packages.lock.json` when one exists, even when
`NuGetLockFilePath` selects a RID-specific file. Do not introduce conventional
lock files beside these three repository-owned graphs.

The gate uses `dotnet restore --locked-mode --force-evaluate`. The forced
evaluation makes the result independent of stale `obj/` assets; locked mode
still prevents rewriting. If a project or central package version disagrees
with a committed lock file, restore fails instead of silently changing the
dependency graph. The launcher's nested Bake publish uses the matching
RID-specific lock and the same forced locked evaluation.

Each restore, build, and test process has an outer hard timeout. Every test
also runs with VSTest blame-hang enabled: after three minutes in one test, the
test host is terminated and a mini dump is collected; after ten minutes, the
outer watchdog kills the complete `dotnet test` process tree. CI additionally
has a 45-minute job bound.

Evidence is written to `artifacts/release-gate/`:

- `release-gate-summary.json` records the commit, branch, worktree state, SDK,
  RID, bounds, process outcomes, assembly list, and
  executed/passed/skipped/failed totals;
- `environment.txt` records `dotnet --info`, configured NuGet sources, and the
  supported project set, package-lock hashes, and discovered test-project set;
- `test-results/` contains one TRX per assembly plus any VSTest hang sequence
  and dump files;
- `logs/` contains the exact command and complete output for every child
  process; and
- `SHA256SUMS.txt` hashes the evidence bundle.

The complete gate runs on Windows because it exercises the full product and
launcher surface. The repository command above is the authoritative gate;
focused portability or Vulkan jobs are not substitutes for it.

The JSON summary records the exact test filter. Environment-dependent,
diagnostic, manual, and known-failure results must be published as their own
lane and must never be added to the hermetic pass headline.

## The Timing lane

`Lane=Timing` marks tests whose outcome depends on **real elapsed time or OS
scheduling** rather than on logic: simulated packet-loss soaks, a virtual-clock
transport session that still waits on wall-clock windows, signalling a real
child process, orphaned-process restart recovery. They pass on an idle machine
and fail intermittently under full-assembly load, so they cannot gate a push
without making the gate untrustworthy.

They are not weakened or deleted — run them deliberately, on a machine that is
not saturated:

```powershell
pwsh ./tools/run-release-gate.ps1 -SkipRestore -SkipBuild `
  -TestFilter 'Lane=Timing&Status!=KnownFailure&Purpose!=Diagnostic'
```

Measured before laning: on the 6-core Linux runner, three stress rounds of the
full suite failed `GracefulStopSignalSendsSigintToARealChildOnLinux` 3/3 (it
passes in ~47 ms alone) and two loss-simulation tests 1/3 each. Chasing them one
at a time did not converge — four separate fixes, each surfacing a different
member of the same family, and one of those fixes regressed the other platform.

Add to this lane only with evidence that a test fails under load and passes in
isolation. A test that fails consistently is a bug, not a timing lane member.

## Continuous integration

This document owns the LOCAL gate. What runs on a push, and how releases are
built, is in [`ci-and-releases.md`](ci-and-releases.md). CI deliberately does
NOT invoke `run-release-gate.ps1`: that script redirects child output to log
files, and a runner fails a task that stops reporting as hung.

## Non-hermetic test lanes

`Lane=Vulkan` owns tests that require a Vulkan loader, a Vulkan 1.3-capable
physical device with a graphics queue, and coherent host-visible readback
memory. The portable Release gate excludes this capability lane. The
`vulkan-hardware` CI job runs it explicitly on a machine with a real device;
missing Vulkan capability is a failure there, not a skip. Run the same
dedicated lane locally on a capable device with:

```powershell
dotnet test tests/AcDream.App.Tests/AcDream.App.Tests.csproj -c Release `
  --filter 'Lane=Vulkan'
```

Installed-DAT tests require an explicit opt-in and a retail DAT directory:

```powershell
$env:ACDREAM_RUN_INSTALLED_DAT_TESTS = '1'
$env:ACDREAM_DAT_DIR = 'C:\path\to\Asherons Call'
pwsh ./tools/run-release-gate.ps1 -SkipRestore -SkipBuild `
  -TestFilter 'Lane=InstalledDat&Status!=KnownFailure&Purpose!=Diagnostic'
```

The prepared-package lane additionally requires a validated `acdream.pak`
beside the DATs or at `ACDREAM_PAK_PATH`:

```powershell
$env:ACDREAM_DAT_DIR = 'C:\path\to\Asherons Call'
$env:ACDREAM_PAK_PATH = 'C:\path\to\acdream.pak'
pwsh ./tools/run-release-gate.ps1 -SkipRestore -SkipBuild `
  -TestFilter 'Lane=PreparedPackage&Status!=KnownFailure&Purpose!=Diagnostic'
```

Regenerate all committed UI fixtures through the one comprehensive manual
generator (the former chat/radar-only generators were redundant):

```powershell
$env:ACDREAM_REGENERATE_UI_FIXTURES = '1'
$env:ACDREAM_DAT_DIR = 'C:\path\to\Asherons Call'
dotnet test tests/AcDream.App.Tests/AcDream.App.Tests.csproj -c Release `
  --filter 'Lane=Manual&ManualTask=FixtureGeneration'
```

The retained live-DAT probes are manual evidence, not InstalledDat regression
contracts. Run each opt-in family independently so a probe command can never
regenerate fixtures as a side effect:

```powershell
$env:ACDREAM_DAT_DIR = 'C:\path\to\Asherons Call'
$env:ACDREAM_PROBE_LIVE_MOUNT = '1'
dotnet test tests/AcDream.App.Tests/AcDream.App.Tests.csproj -c Release `
  --filter 'Lane=Manual&ManualTask=LiveMountProbe'

$env:ACDREAM_PROBE_POWERBAR = '1'
dotnet test tests/AcDream.App.Tests/AcDream.App.Tests.csproj -c Release `
  --filter 'Lane=Manual&ManualTask=PowerbarProbe'
```

Known failures (`Status=KnownFailure`) are never part of a green release total.
Run them explicitly with their prerequisite lane configured; a failure is
expected until the linked defect is fixed. Diagnostic apparatus
(`Purpose=Diagnostic`) likewise reports separately and does not inflate the
contract-test pass count.

The current diagnostic apparatus lives in App and Core. It is retained for
investigation output, and several methods require installed DATs:

```powershell
dotnet test tests/AcDream.App.Tests/AcDream.App.Tests.csproj -c Release `
  --filter 'Purpose=Diagnostic&Lane!=Manual'
dotnet test tests/AcDream.Core.Tests/AcDream.Core.Tests.csproj -c Release `
  --filter 'Purpose=Diagnostic&Lane!=Manual'
```

Operating-system contracts are likewise explicit. Run `Lane=Windows` on a
Windows host and `Lane=Linux` on a native Linux host; a lane is not portable
evidence when executed on the other operating system.

`Lane=SystemFont` exercises the BitmapFont path against a host-provided TTF.
It is separate because the supported runtime can legitimately have none of the
well-known development fonts installed.

## Updating dependencies

Do not edit lock files by hand. To make an intentional dependency change:

1. Change the version once in `Directory.Packages.props` (or add/remove a
   versionless `PackageReference` in a project).
2. Regenerate the neutral graph and both supported release-RID graphs from the
   repository root:

   ```powershell
   pwsh ./tools/update-package-locks.ps1
   ```

3. Review the central-version and `packages.*.lock.json` diffs.
4. Prove locked resolution and run the gate:

   ```powershell
   dotnet restore AcDream.slnx --locked-mode --force-evaluate
   pwsh ./tools/run-release-gate.ps1
   ```
