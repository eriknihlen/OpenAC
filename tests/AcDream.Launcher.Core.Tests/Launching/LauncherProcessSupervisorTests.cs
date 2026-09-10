using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using AcDream.Launcher.Core;
using AcDream.Launcher.Core.Launching;

namespace AcDream.Launcher.Core.Tests.Launching;

public sealed class LauncherProcessSupervisorTests
{
    [Fact]
    [Trait("Lane", "Windows")]
    public void WindowsFactoryUsesNativeProcessGroupsOnlyForConsoleChildren()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Lane=Windows requires a Windows host.");
        }

        var factory = new SystemChildProcessFactory();
        using ILauncherChildProcess console = factory.Create(
            new LauncherProcessSpec("headless.exe", []));
        using ILauncherChildProcess graphical = factory.Create(
            new LauncherProcessSpec(
                "graphical.exe",
                [],
                SupportsConsoleGracefulStop: false));

        Assert.IsType<WindowsSystemChildProcess>(console);
        Assert.IsType<SystemChildProcess>(graphical);
    }

    [Fact]
    public void StartWritesPasswordThenClosesStdinAndTransitionsToRunning()
    {
        var factory = new FakeChildProcessFactory(exitsWithinStopTimeout: true);
        using var supervisor = new LauncherProcessSupervisor(factory);
        var states = new List<LauncherSessionState>();
        supervisor.StateChanged += (_, s) => states.Add(s);

        supervisor.Start(Spec(), "S3cretPassw0rd!");

        FakeChildProcess fake = factory.LastCreated!;
        Assert.True(fake.Started);
        Assert.Equal("S3cretPassw0rd!\n", fake.StandardInputText);
        Assert.True(fake.StandardInputClosed);
        Assert.Equal(LauncherSessionState.Running, supervisor.State);
        Assert.Equal(
            [LauncherSessionState.Starting, LauncherSessionState.Running],
            states);
    }

    [Fact]
    public void StartWithNullPasswordClosesStdinWithoutWriting()
    {
        var factory = new FakeChildProcessFactory(exitsWithinStopTimeout: true);
        using var supervisor = new LauncherProcessSupervisor(factory);

        supervisor.Start(Spec(), password: null);

        FakeChildProcess fake = factory.LastCreated!;
        Assert.Equal(string.Empty, fake.StandardInputText);
        Assert.True(fake.StandardInputClosed);
    }

    [Fact]
    public void StartTwiceOnTheSameSupervisorThrows()
    {
        var factory = new FakeChildProcessFactory(exitsWithinStopTimeout: true);
        using var supervisor = new LauncherProcessSupervisor(factory);
        supervisor.Start(Spec(), "pw");

        Assert.Throws<InvalidOperationException>(() => supervisor.Start(Spec(), "pw"));
    }

    [Fact]
    public void StopCallsCloseMainWindowAndSucceedsWithoutKillWhenTheProcessExitsInTime()
    {
        var factory = new FakeChildProcessFactory(exitsWithinStopTimeout: true);
        using var supervisor = new LauncherProcessSupervisor(factory);
        supervisor.Start(Spec(), "pw");

        supervisor.Stop(TimeSpan.FromMilliseconds(50));

        FakeChildProcess fake = factory.LastCreated!;
        Assert.True(fake.CloseMainWindowCalled);
        Assert.Equal(0, fake.KillCallCount);
        Assert.Equal(LauncherSessionState.Exited, supervisor.State);
        Assert.Equal(0, supervisor.ExitCode);
    }

    [Fact]
    public void StopFallsBackToKillWhenTheProcessDoesNotExitWithinTheTimeout()
    {
        var factory = new FakeChildProcessFactory(exitsWithinStopTimeout: false);
        using var supervisor = new LauncherProcessSupervisor(factory);
        supervisor.Start(Spec(), "pw");

        supervisor.Stop(TimeSpan.FromMilliseconds(50));

        FakeChildProcess fake = factory.LastCreated!;
        Assert.True(fake.CloseMainWindowCalled);
        Assert.Equal(1, fake.KillCallCount);
        Assert.Equal(LauncherSessionState.Exited, supervisor.State);
    }

    [Fact]
    public void StopIsANoOpBeforeStart()
    {
        var factory = new FakeChildProcessFactory(exitsWithinStopTimeout: true);
        using var supervisor = new LauncherProcessSupervisor(factory);

        supervisor.Stop(TimeSpan.FromMilliseconds(50));

        Assert.Null(factory.LastCreated);
        Assert.Equal(LauncherSessionState.Starting, supervisor.State);
    }

    [Fact]
    public void StopIsANoOpAfterTheProcessHasAlreadyExited()
    {
        var factory = new FakeChildProcessFactory(exitsWithinStopTimeout: true);
        using var supervisor = new LauncherProcessSupervisor(factory);
        supervisor.Start(Spec(), "pw");
        supervisor.Stop(TimeSpan.FromMilliseconds(50));
        FakeChildProcess fake = factory.LastCreated!;
        Assert.Equal(0, fake.KillCallCount);

        supervisor.Stop(TimeSpan.FromMilliseconds(50));

        Assert.Equal(1, fake.CloseMainWindowCallCount);
        Assert.Equal(0, fake.KillCallCount);
    }

    [Fact]
    public void StopAttemptsTheGracefulStopSignalBeforeCloseMainWindow()
    {
        var factory = new FakeChildProcessFactory(exitsWithinStopTimeout: true);
        using var supervisor = new LauncherProcessSupervisor(factory);
        supervisor.Start(Spec(), "pw");

        supervisor.Stop(TimeSpan.FromMilliseconds(50));

        FakeChildProcess fake = factory.LastCreated!;
        Assert.Equal(1, fake.TryRequestGracefulStopCallCount);
        Assert.Equal(["gracefulStop", "closeMainWindow"], fake.CallOrder);
    }

    [Fact]
    [Trait("Lane", "Unix")]
    [Trait("Lane", "Timing")]
    public void GracefulStopSignalSendsSigintToARealChildOnUnix()
    {
        if (!LauncherOperatingSystem.IsUnix)
            throw new PlatformNotSupportedException("Lane=Unix requires a native Unix host.");

        string readyMarker = Path.Combine(
            Path.GetTempPath(), "acdream-la3-sigint-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var supervisor = new LauncherProcessSupervisor();
            var exited = new ManualResetEventSlim(false);
            supervisor.StateChanged += (_, s) =>
            {
                if (s == LauncherSessionState.Exited)
                    exited.Set();
            };

            supervisor.Start(
                new LauncherProcessSpec(
                    "/bin/bash",
                    [
                        "-c",
                        "trap 'kill $child 2>/dev/null; exit 0' INT; "
                            + "sleep 30 & child=$!; "
                            + $"touch '{readyMarker}'; "
                            + "wait $child",
                    ]),
                password: null);

            DateTime readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!File.Exists(readyMarker) && DateTime.UtcNow < readyDeadline)
            {
                Thread.Sleep(10);
            }

            Assert.True(
                File.Exists(readyMarker),
                "child did not signal trap-armed readiness in time");

            supervisor.Stop(TimeSpan.FromSeconds(10));

            Assert.True(exited.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, supervisor.ExitCode);
        }
        finally
        {
            try
            {
                File.Delete(readyMarker);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    [Trait("Lane", "Windows")]
    public async Task WindowsCtrlBreakStopsOnlyTheTargetProcessGroupWithoutKill()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Lane=Windows requires a Windows host.");
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            "acdream-la11-ctrl-break",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var targetFactory = new RecordingRealChildProcessFactory();
        var siblingFactory = new RecordingRealChildProcessFactory();
        using var target = new LauncherProcessSupervisor(targetFactory);
        using var sibling = new LauncherProcessSupervisor(siblingFactory);
        string targetReady = Path.Combine(root, "target.ready.json");
        string targetBreak = Path.Combine(root, "target.break");
        string siblingReady = Path.Combine(root, "sibling.ready.json");
        string siblingBreak = Path.Combine(root, "sibling.break");
        string[] exactArguments =
        [
            "plain",
            "contains spaces",
            "quoted-\"value",
            "ends-with-backslash\\",
            string.Empty,
        ];

        try
        {
            target.Start(
                ConsoleFixtureSpec(
                    targetReady,
                    targetBreak,
                    "target",
                    exactArguments),
                "fixture-input");
            sibling.Start(
                ConsoleFixtureSpec(
                    siblingReady,
                    siblingBreak,
                    "sibling",
                    ["sibling"]),
                password: null);
            await WaitForFileAsync(targetReady, targetFactory.LastCreated!);
            await WaitForFileAsync(siblingReady, siblingFactory.LastCreated!);

            using (JsonDocument ready = JsonDocument.Parse(
                       await File.ReadAllTextAsync(targetReady)))
            {
                string[] observed = ready.RootElement
                    .GetProperty("arguments")
                    .EnumerateArray()
                    .Select(value => value.GetString()!)
                    .ToArray();
                Assert.Equal(exactArguments, observed);
                Assert.Equal(
                    "fixture-input\n".Length,
                    ready.RootElement.GetProperty("stdinLength").GetInt32());
                Assert.Equal(
                    1,
                    ready.RootElement.GetProperty("stdinLineCount").GetInt32());
            }

            target.Stop(TimeSpan.FromSeconds(10));

            Assert.Equal(0, target.ExitCode);
            Assert.True(File.Exists(targetBreak),
                "the target fixture did not observe CTRL_BREAK");
            Assert.Equal(0, targetFactory.LastCreated!.KillCallCount);
            Assert.False(siblingFactory.LastCreated!.HasExited);
            Assert.False(File.Exists(siblingBreak),
                "CTRL_BREAK spilled into the sibling process group");

            sibling.Stop(TimeSpan.FromSeconds(10));
            Assert.Equal(0, sibling.ExitCode);
            Assert.True(File.Exists(siblingBreak));
            Assert.Equal(0, siblingFactory.LastCreated!.KillCallCount);
        }
        finally
        {
            targetFactory.LastCreated?.ForceCleanup();
            siblingFactory.LastCreated?.ForceCleanup();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    [Trait("Lane", "Windows")]
    public async Task WindowsConsolelessParentStillTargetsDistinctChildProcessGroups()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Lane=Windows requires a Windows host.");
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            "acdream-la11-consoleless-parent",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string resultPath = Path.Combine(root, "result.json");
        var startInfo = new ProcessStartInfo
        {
            FileName = GetConsolelessParentFixturePath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(resultPath);
        startInfo.ArgumentList.Add(FindDotnetExecutable());
        startInfo.ArgumentList.Add(GetConsoleFixturePath());

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The consoleless supervisor fixture did not start.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await process.WaitForExitAsync(timeout.Token);

            Assert.True(File.Exists(resultPath),
                "the consoleless supervisor fixture did not write its result");
            using JsonDocument result = JsonDocument.Parse(
                await File.ReadAllTextAsync(resultPath));
            Assert.Equal(0, process.ExitCode);
            Assert.False(result.RootElement.GetProperty("parentHadConsoleBefore").GetBoolean());
            Assert.False(result.RootElement.GetProperty("parentHadConsoleAfterStarts").GetBoolean());
            Assert.Equal(0, result.RootElement.GetProperty("targetExitCode").GetInt32());
            Assert.Equal(0, result.RootElement.GetProperty("siblingExitCode").GetInt32());
            Assert.True(result.RootElement.GetProperty("targetBreakObserved").GetBoolean());
            Assert.True(result.RootElement.GetProperty("siblingBreakObserved").GetBoolean());
            Assert.True(result.RootElement.GetProperty("siblingUnaffected").GetBoolean());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void StartKillsAndDisposesTheChildWhenFeedingStdinThrowsAfterTheProcessHasStarted()
    {
        var factory = new FakeChildProcessFactory(
            exitsWithinStopTimeout: true,
            throwOnStandardInputWrite: true);
        using var supervisor = new LauncherProcessSupervisor(factory);

        Assert.ThrowsAny<Exception>(() => supervisor.Start(Spec(), "pw"));

        FakeChildProcess fake = factory.LastCreated!;
        Assert.True(fake.Started);
        Assert.Equal(1, fake.KillCallCount);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void SetStateIsMonotonicAndIgnoresATransitionAfterExited()
    {
        var factory = new FakeChildProcessFactory(
            exitsWithinStopTimeout: true,
            exitDuringStart: true);
        using var supervisor = new LauncherProcessSupervisor(factory);
        var states = new List<LauncherSessionState>();
        supervisor.StateChanged += (_, s) => states.Add(s);

        supervisor.Start(Spec(), "pw");

        Assert.Equal(LauncherSessionState.Exited, supervisor.State);
        Assert.Equal(
            [LauncherSessionState.Starting, LauncherSessionState.Exited],
            states);
    }

    [Fact]
    public async Task ConcurrentRunningAndExitedPublicationsRemainMonotonicAndInOrder()
    {
        var factory = new FakeChildProcessFactory(exitsWithinStopTimeout: true);
        using var supervisor = new LauncherProcessSupervisor(factory);
        using var runningPublicationEntered = new ManualResetEventSlim(false);
        using var releaseRunningPublication = new ManualResetEventSlim(false);
        var states = new ConcurrentQueue<LauncherSessionState>();

        supervisor.StateChanged += (_, state) =>
        {
            if (state == LauncherSessionState.Running)
            {
                runningPublicationEntered.Set();
                Assert.True(
                    releaseRunningPublication.Wait(TimeSpan.FromSeconds(5)),
                    "test did not release the Running publication barrier");
            }

            states.Enqueue(state);
        };

        Task startTask = Task.Run(() => supervisor.Start(Spec(), "pw"));
        try
        {
            Assert.True(
                runningPublicationEntered.Wait(TimeSpan.FromSeconds(5)),
                "Running publication did not reach the test barrier");

            factory.LastCreated!.ExitForTest(17);
            Assert.Equal(LauncherSessionState.Exited, supervisor.State);
        }
        finally
        {
            releaseRunningPublication.Set();
        }

        await startTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            [
                LauncherSessionState.Starting,
                LauncherSessionState.Running,
                LauncherSessionState.Exited,
            ],
            states);
        Assert.Equal(17, supervisor.ExitCode);
    }

    [Fact]
    public void StateChangedPublicationAllowsCrossThreadReadsAndReentrantExit()
    {
        var factory = new FakeChildProcessFactory(exitsWithinStopTimeout: true);
        using var supervisor = new LauncherProcessSupervisor(factory);
        var states = new List<LauncherSessionState>();

        supervisor.StateChanged += (_, state) =>
        {
            states.Add(state);
            if (state != LauncherSessionState.Running)
            {
                return;
            }

            Task<LauncherSessionState> readTask = Task.Run(() => supervisor.State);
            Assert.True(readTask.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(LauncherSessionState.Running, readTask.Result);
            factory.LastCreated!.ExitForTest(23);
        };

        supervisor.Start(Spec(), "pw");

        Assert.Equal(
            [
                LauncherSessionState.Starting,
                LauncherSessionState.Running,
                LauncherSessionState.Exited,
            ],
            states);
        Assert.Equal(LauncherSessionState.Exited, supervisor.State);
        Assert.Equal(23, supervisor.ExitCode);
    }

    [Fact]
    public async Task DisposeAllowsAnAlreadyCapturedExitCallbackToComplete()
    {
        var factory = new ExitDisposeRaceChildProcessFactory();
        var supervisor = new LauncherProcessSupervisor(factory);
        var states = new ConcurrentQueue<LauncherSessionState>();
        supervisor.StateChanged += (_, state) => states.Enqueue(state);

        supervisor.Start(Spec(), password: null);
        ExitDisposeRaceChildProcess child = factory.LastCreated!;
        child.BeginExit(47);
        Assert.True(
            child.ExitCallbackReady.Wait(TimeSpan.FromSeconds(5)),
            "the captured exit callback did not reach its barrier");

        Task disposeTask = Task.Run(supervisor.Dispose);
        try
        {
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            child.ReleaseDisposeForCleanup();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
            await child.ExitCallbackTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(child.Disposed);
        Assert.Equal(LauncherSessionState.Exited, supervisor.State);
        Assert.Equal(47, supervisor.ExitCode);
        Assert.Equal(
            [
                LauncherSessionState.Starting,
                LauncherSessionState.Running,
                LauncherSessionState.Exited,
            ],
            states);

        supervisor.Dispose();
        Assert.Equal(1, child.DisposeCallCount);
        Assert.Single(states, state => state == LauncherSessionState.Exited);
    }

    [Fact]
    public void LauncherProcessSpecCarriesNoCredentialLikeMember()
    {
        System.Reflection.PropertyInfo[] properties =
            typeof(LauncherProcessSpec).GetProperties();
        Assert.DoesNotContain(
            properties,
            p => p.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RealProcessSpawnFeedsStdinAndCapturesExitCode()
    {
        string dotnet = FindDotnetExecutable();
        using var supervisor = new LauncherProcessSupervisor();
        var exited = new ManualResetEventSlim(false);
        supervisor.StateChanged += (_, s) =>
        {
            if (s == LauncherSessionState.Exited)
                exited.Set();
        };

        supervisor.Start(
            new LauncherProcessSpec(dotnet, ["--version"]),
            "unused-password-ignored-by-dotnet");

        bool completed = exited.Wait(TimeSpan.FromSeconds(30));

        Assert.True(completed, "the real dotnet --version child did not exit within 30s");
        Assert.Equal(0, supervisor.ExitCode);
    }

    [Fact]
    public async Task RealChildStderrIsCapturedForTheProcessStartInfoPath()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "acdream-406-stderr-psi",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string stderrPath = Path.Combine(root, "client.err.log");

        try
        {
            using var supervisor = new LauncherProcessSupervisor();
            var exited = new ManualResetEventSlim(false);
            supervisor.StateChanged += (_, s) =>
            {
                if (s == LauncherSessionState.Exited)
                    exited.Set();
            };

            supervisor.Start(
                new LauncherProcessSpec(
                    FindDotnetExecutable(),
                    [GetConsoleFixturePath(), "write-stderr", "7", "3", "line-"],
                    SupportsConsoleGracefulStop: false,
                    StderrLogPath: stderrPath),
                password: null);

            Assert.True(
                exited.Wait(TimeSpan.FromSeconds(30)),
                "the write-stderr fixture did not exit within 30s");
            Assert.Equal(7, supervisor.ExitCode);

            string captured = await ReadFileEventuallyContainingAsync(
                stderrPath, "line-2", TimeSpan.FromSeconds(5));
            Assert.Contains("line-0", captured, StringComparison.Ordinal);
            Assert.Contains("line-1", captured, StringComparison.Ordinal);
            Assert.Contains("line-2", captured, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    [Trait("Lane", "Windows")]
    public async Task GraphicalLaunchHasNoVisibleConsoleAndPreservesRedirectedStreams()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Lane=Windows requires a Windows host.");
        }

        string root = Path.Combine(Path.GetTempPath(), "acdream-console-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string stderrPath = Path.Combine(root, "client.err.log");
        try
        {
            using var supervisor = new LauncherProcessSupervisor();
            using var exited = new ManualResetEventSlim(false);
            supervisor.StateChanged += (_, state) =>
            {
                if (state == LauncherSessionState.Exited)
                    exited.Set();
            };
            supervisor.Start(
                new LauncherProcessSpec(
                    FindDotnetExecutable(),
                    [GetConsoleFixturePath(), "check-no-console"],
                    SupportsConsoleGracefulStop: false,
                    StderrLogPath: stderrPath),
                password: "test-input");

            Assert.True(exited.Wait(TimeSpan.FromSeconds(30)), "The console probe did not exit.");
            Assert.Equal(0, supervisor.ExitCode);
            string captured = await ReadFileEventuallyContainingAsync(
                stderrPath, "stdinLength=11", TimeSpan.FromSeconds(5));
            Assert.Contains("hasVisibleConsole=False;stdinLength=11", captured, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Lane", "Windows")]
    public async Task RealChildStderrIsCapturedForTheWindowsNativeConsolePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Lane=Windows requires a Windows host.");
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            "acdream-406-stderr-native",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string stderrPath = Path.Combine(root, "client.err.log");

        try
        {
            using var supervisor = new LauncherProcessSupervisor();
            var exited = new ManualResetEventSlim(false);
            supervisor.StateChanged += (_, s) =>
            {
                if (s == LauncherSessionState.Exited)
                    exited.Set();
            };

            supervisor.Start(
                new LauncherProcessSpec(
                    FindDotnetExecutable(),
                    [GetConsoleFixturePath(), "write-stderr", "9", "3", "native-line-"],
                    StderrLogPath: stderrPath),
                password: null);

            Assert.True(
                exited.Wait(TimeSpan.FromSeconds(30)),
                "the write-stderr fixture did not exit within 30s");
            Assert.Equal(9, supervisor.ExitCode);

            string captured = await ReadFileEventuallyContainingAsync(
                stderrPath, "native-line-2", TimeSpan.FromSeconds(5));
            Assert.Contains("native-line-0", captured, StringComparison.Ordinal);
            Assert.Contains("native-line-1", captured, StringComparison.Ordinal);
            Assert.Contains("native-line-2", captured, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void ANullStderrLogPathBehavesExactlyAsBeforeForBothChildProcessKinds()
    {
        string dotnet = FindDotnetExecutable();
        using var supervisor = new LauncherProcessSupervisor();
        var exited = new ManualResetEventSlim(false);
        supervisor.StateChanged += (_, s) =>
        {
            if (s == LauncherSessionState.Exited)
                exited.Set();
        };

        supervisor.Start(new LauncherProcessSpec(dotnet, ["--version"]), null);

        Assert.True(exited.Wait(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, supervisor.ExitCode);
    }

    private static async Task<string> ReadFileEventuallyContainingAsync(
        string path,
        string expectedFragment,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        string last = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    last = await reader.ReadToEndAsync();
                    if (last.Contains(expectedFragment, StringComparison.Ordinal))
                    {
                        return last;
                    }
                }
                catch (IOException)
                {
                    // The pump/writer may hold the file open for a
                    // moment — retry within the deadline.
                }
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"'{path}' never contained '{expectedFragment}' within {timeout}. "
                + $"Last observed content: {last}");
    }

    private static LauncherProcessSpec Spec() =>
        new("fake-host", ["--session-config", "session.json"]);

    private static LauncherProcessSpec ConsoleFixtureSpec(
        string ready,
        string breakMarker,
        string label,
        IReadOnlyList<string> exactArguments) =>
        new(
            FindDotnetExecutable(),
            [
                GetConsoleFixturePath(),
                "wait-for-break",
                ready,
                breakMarker,
                label,
                .. exactArguments,
            ]);

    private static string FindDotnetExecutable() =>
        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    private static string GetConsoleFixturePath()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name ?? "Release";
        return Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "AcDream.Launcher.Core.Tests.Fixtures.ConsoleSignalChild",
            "bin",
            configuration,
            "net10.0",
            "AcDream.Launcher.Core.Tests.Fixtures.ConsoleSignalChild.dll");
    }

    private static string GetConsolelessParentFixturePath()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name ?? "Release";
        return Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "AcDream.Launcher.Core.Tests.Fixtures.ConsolelessSupervisorParent",
            "bin",
            configuration,
            "net10.0",
            "AcDream.Launcher.Core.Tests.Fixtures.ConsolelessSupervisorParent.exe");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private static async Task WaitForFileAsync(
        string path,
        RecordingChildProcess child)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!File.Exists(path))
        {
            if (child.HasExited)
            {
                throw new InvalidOperationException(
                    $"Console fixture exited early with {child.ExitCode}.");
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Console fixture did not become ready.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class RecordingRealChildProcessFactory : ILauncherChildProcessFactory
    {
        private readonly SystemChildProcessFactory _inner = new();

        internal RecordingChildProcess? LastCreated { get; private set; }

        public ILauncherChildProcess Create(LauncherProcessSpec spec)
        {
            LastCreated = new RecordingChildProcess(_inner.Create(spec));
            return LastCreated;
        }
    }

    private sealed class RecordingChildProcess(ILauncherChildProcess inner)
        : ILauncherChildProcess
    {
        public int KillCallCount { get; private set; }

        public bool HasExited => inner.HasExited;

        public int ExitCode => inner.ExitCode;

        public TextWriter StandardInput => inner.StandardInput;

        public event EventHandler? Exited
        {
            add => inner.Exited += value;
            remove => inner.Exited -= value;
        }

        public void Start() => inner.Start();

        public bool TryRequestGracefulStop() => inner.TryRequestGracefulStop();

        public bool CloseMainWindow() => inner.CloseMainWindow();

        public void Kill()
        {
            KillCallCount++;
            inner.Kill();
        }

        public bool WaitForExit(TimeSpan timeout) => inner.WaitForExit(timeout);

        public void ForceCleanup()
        {
            try
            {
                if (!HasExited)
                {
                    inner.Kill();
                    _ = inner.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
            catch
            {
            }
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class FakeChildProcessFactory(
        bool exitsWithinStopTimeout,
        bool exitDuringStart = false,
        bool throwOnStandardInputWrite = false)
        : ILauncherChildProcessFactory
    {
        public FakeChildProcess? LastCreated { get; private set; }

        public ILauncherChildProcess Create(LauncherProcessSpec spec)
        {
            LastCreated = new FakeChildProcess(
                spec,
                exitsWithinStopTimeout,
                exitDuringStart,
                throwOnStandardInputWrite);
            return LastCreated;
        }
    }

    private sealed class ExitDisposeRaceChildProcessFactory
        : ILauncherChildProcessFactory
    {
        public ExitDisposeRaceChildProcess? LastCreated { get; private set; }

        public ILauncherChildProcess Create(LauncherProcessSpec spec)
        {
            LastCreated = new ExitDisposeRaceChildProcess();
            return LastCreated;
        }
    }

    private sealed class ExitDisposeRaceChildProcess : ILauncherChildProcess
    {
        private readonly StringWriter _standardInput = new();
        private readonly ManualResetEventSlim _exitCallbackReady = new(false);
        private readonly ManualResetEventSlim _releaseExitCallback = new(false);
        private readonly ManualResetEventSlim _exitCallbackReturned = new(false);
        private readonly ManualResetEventSlim _releaseDisposeForCleanup = new(false);
        private EventHandler? _exited;

        public bool HasExited { get; private set; }

        public int ExitCode { get; private set; }

        public TextWriter StandardInput => _standardInput;

        public bool Disposed { get; private set; }

        public int DisposeCallCount { get; private set; }

        public ManualResetEventSlim ExitCallbackReady => _exitCallbackReady;

        public Task ExitCallbackTask { get; private set; } = Task.CompletedTask;

        public event EventHandler? Exited
        {
            add => _exited += value;
            remove => _exited -= value;
        }

        public void Start()
        {
        }

        public void BeginExit(int exitCode)
        {
            HasExited = true;
            ExitCode = exitCode;
            EventHandler? captured = _exited;
            ExitCallbackTask = Task.Run(() =>
            {
                _exitCallbackReady.Set();
                _releaseExitCallback.Wait();
                try
                {
                    captured?.Invoke(this, EventArgs.Empty);
                }
                finally
                {
                    _exitCallbackReturned.Set();
                }
            });
        }

        public bool TryRequestGracefulStop() => false;

        public bool CloseMainWindow() => false;

        public void Kill()
        {
            throw new InvalidOperationException(
                "the already-exited race child must not be killed");
        }

        public bool WaitForExit(TimeSpan timeout) => HasExited;

        public void ReleaseDisposeForCleanup() =>
            _releaseDisposeForCleanup.Set();

        public void Dispose()
        {
            DisposeCallCount++;
            _releaseExitCallback.Set();
            _ = WaitHandle.WaitAny(
                [
                    _exitCallbackReturned.WaitHandle,
                    _releaseDisposeForCleanup.WaitHandle,
                ]);
            Disposed = true;
            _standardInput.Dispose();
        }
    }

    private sealed class FakeChildProcess(
        LauncherProcessSpec spec,
        bool exitsWithinStopTimeout,
        bool exitDuringStart = false,
        bool throwOnStandardInputWrite = false)
        : ILauncherChildProcess
    {
        private readonly RecordingTextWriter _standardInput = new();
        private readonly ThrowingTextWriter _throwingStandardInput = new();

        public LauncherProcessSpec Spec { get; } = spec;

        public bool Started { get; private set; }

        public string StandardInputText => _standardInput.ToString();

        public bool StandardInputClosed => _standardInput.IsClosed;

        public bool CloseMainWindowCalled => CloseMainWindowCallCount > 0;

        public int CloseMainWindowCallCount { get; private set; }

        public int TryRequestGracefulStopCallCount { get; private set; }

        public int KillCallCount { get; private set; }

        public bool Disposed { get; private set; }

        public List<string> CallOrder { get; } = [];

        public bool HasExited { get; private set; }

        public int ExitCode { get; private set; }

        public TextWriter StandardInput =>
            throwOnStandardInputWrite ? _throwingStandardInput : _standardInput;

        public event EventHandler? Exited;

        public void Start()
        {
            Started = true;

            if (exitDuringStart)
            {
                ExitForTest(0);
            }
        }

        public void ExitForTest(int exitCode)
        {
            if (HasExited)
            {
                return;
            }

            HasExited = true;
            ExitCode = exitCode;
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public bool TryRequestGracefulStop()
        {
            TryRequestGracefulStopCallCount++;
            CallOrder.Add("gracefulStop");
            return false;
        }

        public bool CloseMainWindow()
        {
            CloseMainWindowCallCount++;
            CallOrder.Add("closeMainWindow");
            return true;
        }

        public void Kill()
        {
            KillCallCount++;
            CallOrder.Add("kill");
            ExitForTest(-1);
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            if (!exitsWithinStopTimeout)
                return false;

            ExitForTest(0);
            return true;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class RecordingTextWriter : StringWriter
    {
        public bool IsClosed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsClosed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingTextWriter : StringWriter
    {
        public override void Write(string? value) =>
            throw new IOException("simulated broken stdin pipe");

        public override void Write(char value) =>
            throw new IOException("simulated broken stdin pipe");
    }
}
