using System.Diagnostics;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Tests.Updates;

public sealed class UpdateSessionBarrierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-update-lease-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SharedSessionsCoexistAndExcludeUpdateTransactions()
    {
        var barrier = new UpdateSessionBarrier(_root);
        using UpdateSessionBarrier.SessionLease first = barrier.AcquireSession();
        using UpdateSessionBarrier.SessionLease second = barrier.AcquireSession();

        LauncherUpdateException blocked = Assert.Throws<LauncherUpdateException>(
            barrier.AcquireExclusive);

        Assert.Contains("session", blocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExclusiveUpdaterExcludesSessionsAndConcurrentUpdater()
    {
        var barrier = new UpdateSessionBarrier(_root);
        using UpdateSessionBarrier.ExclusiveLease update = barrier.AcquireExclusive();

        Assert.Throws<LauncherUpdateException>(barrier.AcquireSession);
        Assert.Throws<LauncherUpdateException>(barrier.AcquireExclusive);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("exclusive")]
    public async Task CrossProcessLeaseRefusesRacingLauncherAndReleasesCleanly(string mode)
    {
        string ready = Path.Combine(_root, mode + ".ready");
        string release = Path.Combine(_root, mode + ".release");
        Directory.CreateDirectory(_root);
        string fixture = GetFixturePath();
        Assert.True(File.Exists(fixture), $"Missing fixture: {fixture}");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add(fixture);
        startInfo.ArgumentList.Add("hold-update-lease");
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(_root);
        startInfo.ArgumentList.Add(ready);
        startInfo.ArgumentList.Add(release);
        using Process holder = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start update lease fixture.");
        try
        {
            await WaitForFileAsync(ready, holder);
            var barrier = new UpdateSessionBarrier(_root);
            Assert.Throws<LauncherUpdateException>(barrier.AcquireExclusive);
            if (mode == "session")
            {
                using UpdateSessionBarrier.SessionLease peer = barrier.AcquireSession();
            }
            else
            {
                Assert.Throws<LauncherUpdateException>(barrier.AcquireSession);
            }

            await File.WriteAllTextAsync(release, "release");
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, holder.ExitCode);
            using UpdateSessionBarrier.ExclusiveLease after = barrier.AcquireExclusive();
        }
        finally
        {
            if (!holder.HasExited)
            {
                holder.Kill(entireProcessTree: true);
                await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static async Task WaitForFileAsync(string path, Process process)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Lease fixture exited early with {process.ExitCode}: "
                    + await process.StandardError.ReadToEndAsync());
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Lease fixture did not become ready.");
            }

            await Task.Delay(20);
        }
    }

    private static string GetFixturePath()
    {
        string root = FindRepositoryRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name ?? "Release";
        return Path.Combine(
            root,
            "tests",
            "AcDream.Launcher.Core.Tests.Fixtures.InstallLeaseHolder",
            "bin",
            configuration,
            "net10.0",
            "AcDream.Launcher.Core.Tests.Fixtures.InstallLeaseHolder.dll");
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
}
