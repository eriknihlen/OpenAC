using System.Diagnostics;
using System.Reflection;
using AcDream.Bake;
using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

const string SelfUpdateDataEnvironment = "ACDREAM_SELF_UPDATE_FIXTURE_DATA";
const string SelfUpdateTargetEnvironment = "ACDREAM_SELF_UPDATE_FIXTURE_TARGET";
const string SelfUpdateHelperPidEnvironment = "ACDREAM_SELF_UPDATE_FIXTURE_HELPER_PID";

string[] effectiveArgs = args;
string? selfUpdateData = Environment.GetEnvironmentVariable(SelfUpdateDataEnvironment);
string? selfUpdateTarget = Environment.GetEnvironmentVariable(SelfUpdateTargetEnvironment);
if (!string.IsNullOrWhiteSpace(selfUpdateData)
    && !string.IsNullOrWhiteSpace(selfUpdateTarget)
    && IsBootstrapInvocation(effectiveArgs))
{
    if (effectiveArgs[0] == LauncherSelfUpdateBootstrap.HelperArgument
        && Environment.GetEnvironmentVariable(SelfUpdateHelperPidEnvironment) is string helperPid
        && !string.IsNullOrWhiteSpace(helperPid))
    {
        File.WriteAllText(
            Path.GetFullPath(helperPid),
            Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
    }

    using var http = new HttpClient();
    var manager = new LauncherSelfUpdateManager(Paths(selfUpdateData), http);
    SelfUpdateStartupResult startup;
    try
    {
        startup = await LauncherSelfUpdateBootstrap.HandleAsync(
            effectiveArgs,
            manager,
            Path.GetFullPath(AppContext.BaseDirectory),
            Path.GetFullPath(
                Environment.ProcessPath
                ?? throw new InvalidOperationException("Process path is unavailable.")));
    }
    catch (LauncherUpdateException)
    {
        return 74;
    }
    if (startup.ShouldExit)
    {
        return startup.ExitCode;
    }

    effectiveArgs = startup.RemainingArguments;
}

return effectiveArgs.FirstOrDefault() switch
{
    "hold-install-lease" => await HoldInstallLeaseAsync(effectiveArgs[1..]),
    "hold-update-lease" => await HoldUpdateLeaseAsync(effectiveArgs[1..]),
    "orphan-parent" => await RunOrphanParentAsync(effectiveArgs[1..]),
    "orphan-child" => RunOrphanChild(effectiveArgs[1..]),
    "crash-self-update" => await CrashSelfUpdateAsync(effectiveArgs[1..]),
    "stage-self-update" => await StageSelfUpdateAsync(effectiveArgs[1..]),
    "bootstrap-probe" => await BootstrapProbeAsync(effectiveArgs[1..]),
    "canonical-probe" => CanonicalProbe(effectiveArgs[1..]),
    "hold-campaign-la-process" =>
        await HoldCampaignLaProcessAsync(effectiveArgs[1..]),
    _ => 2,
};

static bool IsBootstrapInvocation(string[] arguments) =>
    arguments.Length > 0
    && arguments[0] is LauncherSelfUpdateBootstrap.HelperArgument
        or LauncherSelfUpdateBootstrap.ConfirmArgument
        or "--acdream-self-update-deferred-v1"
        or "canonical-probe";

static ApplicationPathSet Paths(string dataDirectory)
{
    string data = Path.GetFullPath(dataDirectory);
    return new ApplicationPathSet(
        Path.Combine(data, "fixture-config"),
        data,
        Path.Combine(data, "fixture-cache"),
        null);
}

static async Task<int> CrashSelfUpdateAsync(string[] arguments)
{
    if (arguments.Length != 4)
    {
        return 2;
    }

    string dataDirectory = Path.GetFullPath(arguments[0]);
    string targetDirectory = Path.GetFullPath(arguments[1]);
    string readyPath = Path.GetFullPath(arguments[2]);
    string canonicalName = arguments[3];
    using var http = new HttpClient();
    var manager = new LauncherSelfUpdateManager(
        Paths(dataDirectory),
        http,
        null,
        observation =>
        {
            if (observation.Boundary == SelfUpdateApplyBoundary.AfterTargetMutation
                && string.Equals(
                    observation.Path,
                    canonicalName,
                    StringComparison.Ordinal))
            {
                if (!File.Exists(Path.Combine(targetDirectory, canonicalName)))
                {
                    throw new InvalidOperationException(
                        "The canonical launcher vanished at the apply boundary.");
                }

                File.WriteAllText(readyPath, Environment.ProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                Thread.Sleep(Timeout.Infinite);
            }
        });
    using UpdateSessionBarrier.ExclusiveLease lease = manager.Barrier.AcquireExclusive();
    _ = await manager.ApplyPendingAsync(targetDirectory);
    return 0;
}

static async Task<int> StageSelfUpdateAsync(string[] arguments)
{
    if (arguments.Length != 7
        || !long.TryParse(
            arguments[6],
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out long size))
    {
        return 2;
    }

    string dataDirectory = Path.GetFullPath(arguments[0]);
    using var http = new HttpClient();
    var manager = new LauncherSelfUpdateManager(Paths(dataDirectory), http);
    _ = await manager.StageAsync(
        LauncherVersion.Parse(arguments[2]),
        arguments[3],
        new ReleaseArtifact(new Uri(arguments[4]), arguments[5], size),
        Path.GetFullPath(arguments[1]),
        progress: null,
        CancellationToken.None);
    return 0;
}

static async Task<int> BootstrapProbeAsync(string[] arguments)
{
    if (arguments.Length != 4)
    {
        return 2;
    }

    using var http = new HttpClient();
    var manager = new LauncherSelfUpdateManager(Paths(arguments[0]), http);
    SelfUpdateStartupResult result;
    try
    {
        result = await LauncherSelfUpdateBootstrap.HandleAsync(
            ["ordinary"],
            manager,
            Path.GetFullPath(arguments[1]),
            Path.GetFullPath(arguments[2]));
    }
    catch (LauncherUpdateException)
    {
        return 74;
    }
    File.WriteAllText(
        Path.GetFullPath(arguments[3]),
        result.ShouldExit ? "exit" : string.Join("\n", result.RemainingArguments));
    return result.ShouldExit ? 3 : 0;
}

static int CanonicalProbe(string[] arguments)
{
    if (arguments.Length < 1)
    {
        return 2;
    }

    string suffix = arguments.Length == 1
        ? string.Empty
        : Environment.NewLine
            + string.Join(Environment.NewLine, arguments[1..]);
    File.WriteAllText(
        Path.GetFullPath(arguments[0]),
        Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "|"
        + Path.GetFullPath(
            Environment.ProcessPath
            ?? throw new InvalidOperationException("Process path is unavailable."))
        + suffix);
    return 0;
}

static async Task<int> HoldCampaignLaProcessAsync(string[] arguments)
{
    if (arguments.Length != 4
        || arguments[0] is not ("--config" or "--session-config"))
    {
        return 2;
    }

    string configPath = Path.GetFullPath(arguments[1]);
    string readyPath = Path.GetFullPath(arguments[2]);
    string releasePath = Path.GetFullPath(arguments[3]);
    if (!File.Exists(configPath))
    {
        return 3;
    }

    File.WriteAllText(
        readyPath,
        Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
    while (!File.Exists(releasePath))
    {
        await Task.Delay(10);
    }

    return 0;
}

static async Task<int> HoldUpdateLeaseAsync(string[] arguments)
{
    if (arguments.Length != 4
        || arguments[0] is not ("session" or "exclusive"))
    {
        return 2;
    }

    var barrier = new UpdateSessionBarrier(Path.GetFullPath(arguments[1]));
    using IDisposable lease = arguments[0] == "session"
        ? barrier.AcquireSession()
        : barrier.AcquireExclusive();
    string readyPath = Path.GetFullPath(arguments[2]);
    string releasePath = Path.GetFullPath(arguments[3]);
    File.WriteAllText(readyPath, arguments[0]);
    while (!File.Exists(releasePath))
    {
        await Task.Delay(10);
    }

    return 0;
}

static async Task<int> HoldInstallLeaseAsync(string[] arguments)
{
    if (arguments.Length != 3)
    {
        return 2;
    }

    string lockPath = Path.GetFullPath(arguments[0]);
    string stagingPath = Path.GetFullPath(arguments[1]);
    string readyPath = Path.GetFullPath(arguments[2]);
    Directory.CreateDirectory(
        Path.GetDirectoryName(lockPath)
        ?? throw new InvalidOperationException("lock path has no parent"));
    Directory.CreateDirectory(
        Path.GetDirectoryName(stagingPath)
        ?? throw new InvalidOperationException("staging path has no parent"));

    using var lease = new FileStream(
        lockPath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None);
    File.WriteAllText(stagingPath, "abandoned bake staging");
    File.WriteAllText(readyPath, "ready");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

static async Task<int> RunOrphanParentAsync(string[] arguments)
{
    if (arguments.Length != 8)
    {
        return 2;
    }

    string dataDirectory = Path.GetFullPath(arguments[0]);
    string datDirectory = Path.GetFullPath(arguments[1]);
    string bakeMarker = Path.GetFullPath(arguments[2]);
    string schedule = arguments[3];
    string childReadyPath = Path.GetFullPath(arguments[4]);
    string childReleasePath = Path.GetFullPath(arguments[5]);
    string childPidPath = Path.GetFullPath(arguments[6]);
    string childExitPath = Path.GetFullPath(arguments[7]);
    var paths = new ApplicationPathSet(
        Path.Combine(dataDirectory, "fixture-config"),
        dataDirectory,
        Path.Combine(dataDirectory, "fixture-cache"),
        null);
    var runner = new OrphanBakeProcessRunner(
        schedule,
        childReadyPath,
        childReleasePath,
        childPidPath,
        childExitPath);
    var installer = new LauncherInstaller(
        paths,
        bakeMarker,
        processRunner: runner);

    try
    {
        await installer.InstallAsync(datDirectory, 1);
        return 0;
    }
    catch
    {
        return 9;
    }
}

static int RunOrphanChild(string[] arguments)
{
    if (arguments.Length != 5)
    {
        return 2;
    }

    string outputPath = Path.GetFullPath(arguments[0]);
    string schedule = arguments[1];
    string readyPath = Path.GetFullPath(arguments[2]);
    string releasePath = Path.GetFullPath(arguments[3]);
    string exitPath = Path.GetFullPath(arguments[4]);
    Action barrier = () =>
    {
        File.WriteAllText(readyPath, schedule);
        while (!File.Exists(releasePath))
        {
            Thread.Sleep(10);
        }
    };

    int exitCode;
    try
    {
        BakeOutputTransaction.WriteValidateAndPublish(
            outputPath,
            temporaryPath =>
            {
                File.WriteAllText(temporaryPath, "orphan replacement");
                return 1;
            },
            (temporaryPath, _) =>
            {
                if (File.ReadAllText(temporaryPath) != "orphan replacement")
                {
                    throw new InvalidDataException("staging content changed");
                }
            },
            beforePublicationLock: schedule == "late" ? barrier : null,
            beforePromotion: schedule == "holds" ? barrier : null,
            CancellationToken.None);
        exitCode = 0;
    }
    catch (Exception ex)
    {
        File.WriteAllText(exitPath + ".error", ex.Message);
        exitCode = 17;
    }

    File.WriteAllText(exitPath, exitCode.ToString(
        System.Globalization.CultureInfo.InvariantCulture));
    return exitCode;
}

file sealed class OrphanBakeProcessRunner(
    string schedule,
    string childReadyPath,
    string childReleasePath,
    string childPidPath,
    string childExitPath) : IBakeProcessRunner
{
    public async Task<BakeProcessResult> RunAsync(
        BakeProcessRequest request,
        Action<string> onStandardOutput,
        CancellationToken cancellationToken = default)
    {
        string dotnetHost = Environment.ProcessPath
            ?? throw new InvalidOperationException("dotnet host path is unavailable");
        string fixtureDll = Assembly.GetExecutingAssembly().Location;
        var startInfo = new ProcessStartInfo(dotnetHost)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(fixtureDll);
        startInfo.ArgumentList.Add("orphan-child");
        startInfo.ArgumentList.Add(request.OutputPath);
        startInfo.ArgumentList.Add(schedule);
        startInfo.ArgumentList.Add(childReadyPath);
        startInfo.ArgumentList.Add(childReleasePath);
        startInfo.ArgumentList.Add(childExitPath);
        startInfo.Environment.Remove(
            BakePublicationGuardPaths.NonceEnvironmentVariable);
        startInfo.Environment[
            BakePublicationGuardPaths.NonceEnvironmentVariable] =
            request.PublicationNonce
            ?? throw new InvalidOperationException("publication nonce is missing");

        using Process child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("orphan child did not start");
        File.WriteAllText(
            childPidPath,
            child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await child.WaitForExitAsync(cancellationToken);
        if (child.ExitCode == 0)
        {
            long bytes = new FileInfo(request.OutputPath).Length;
            onStandardOutput("{\"v\":1,\"e\":\"started\",\"bakeToolVersion\":4}\n");
            onStandardOutput($"{{\"v\":1,\"e\":\"completed\","
                + $"\"bakeToolVersion\":4,\"outputBytes\":{bytes},"
                + "\"failures\":0}\n");
        }

        return new BakeProcessResult(child.ExitCode, "orphan fixture child");
    }
}
