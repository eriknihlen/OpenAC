using System.Diagnostics;
using AcDream.Launcher.Core;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Updates;
using AcDream.Tests.Fixtures.PluginIcons;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class DirectInstallCheckTests : IDisposable
{
    private const string Id = "acme.plugin";
    private const string EntryDll = "acme.plugin.dll";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-direct-install-check-tests",
        Guid.NewGuid().ToString("N"));

    // Windows refuses a recursive delete that meets a junction, so remove links without following them.
    public void Dispose() => SafeZipExtractor.TryDeleteDirectory(_root);

    [Fact]
    public void AValidFolderPasses()
    {
        string directory = WriteValidPlugin("valid");

        Assert.Null(DirectInstallCheck.Refusal(directory, ValidManifest()));
    }

    [Fact]
    public void FinderAndExplorerMetadataFilesAreIgnored()
    {
        string directory = WriteValidPlugin("ignored");
        File.WriteAllBytes(Path.Combine(directory, ".DS_Store"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(directory, "._acme.plugin.dll"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(directory, "Thumbs.db"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(directory, "desktop.ini"), [1, 2, 3]);

        Assert.Null(DirectInstallCheck.Refusal(directory, ValidManifest()));
    }

    [Fact]
    public void ALinkedRootRefuses()
    {
        string target = WriteValidPlugin("real");
        string link = Path.Combine(_root, "linked-root");
        CreateDirectoryLink(link, target);

        Assert.NotNull(DirectInstallCheck.Refusal(link, ValidManifest()));
    }

    [Fact]
    public void ALinkedSubfolderRefuses()
    {
        string directory = WriteValidPlugin("with-linked-subfolder");
        string linkTarget = Path.Combine(_root, "outside-target");
        Directory.CreateDirectory(linkTarget);
        CreateDirectoryLink(Path.Combine(directory, "assets"), linkTarget);

        Assert.NotNull(DirectInstallCheck.Refusal(directory, ValidManifest()));
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public void AFileSymlinkRefuses()
    {
        if (!LauncherOperatingSystem.IsUnix)
        {
            throw new PlatformNotSupportedException("Lane=Unix requires a native Unix host.");
        }

        string directory = WriteValidPlugin("with-file-symlink");
        string target = Path.Combine(_root, "outside-file.txt");
        File.WriteAllText(target, "not part of the plugin");
        File.CreateSymbolicLink(Path.Combine(directory, "notes.txt"), target);

        Assert.NotNull(DirectInstallCheck.Refusal(directory, ValidManifest()));
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public async Task AFifoRefusesWithoutBlocking()
    {
        if (!LauncherOperatingSystem.IsUnix)
        {
            throw new PlatformNotSupportedException("Lane=Unix requires a native Unix host.");
        }

        string directory = WriteValidPlugin("with-fifo");
        RunMkfifo(Path.Combine(directory, "icon.png"));

        Task<string?> check = Task.Run(() => DirectInstallCheck.Refusal(directory, ValidManifest()));

        string? refusal = await check.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(refusal);
    }

    [Fact]
    public void AnIllegalPathCharacterRefuses()
    {
        string directory = WriteValidPlugin("illegal-character");
        File.WriteAllBytes(Path.Combine(directory, "notes:txt"), [1]);

        Assert.NotNull(DirectInstallCheck.Refusal(directory, ValidManifest()));
    }

    private sealed class CaseSensitiveFactAttribute : FactAttribute
    {
        public CaseSensitiveFactAttribute()
        {
            if (!LauncherOperatingSystem.IsUnix)
            {
                Skip = "Lane=Unix requires a native Unix host.";
                return;
            }

            if (!ProbeCaseSensitivity())
            {
                Skip = "the host filesystem does not distinguish names by case.";
            }
        }

        private static bool ProbeCaseSensitivity()
        {
            string probe = Path.Combine(
                Path.GetTempPath(), "acdream-case-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(probe);
            try
            {
                Directory.CreateDirectory(Path.Combine(probe, "a"));
                Directory.CreateDirectory(Path.Combine(probe, "A"));
                return Directory.EnumerateDirectories(probe).Count() == 2;
            }
            finally
            {
                Directory.Delete(probe, recursive: true);
            }
        }
    }

    [CaseSensitiveFact]
    [Trait("Lane", "Unix")]
    public void CaseCollidingPathsRefuse()
    {
        string directory = WriteValidPlugin("case-collision");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        Directory.CreateDirectory(Path.Combine(directory, "SUB"));
        File.WriteAllBytes(Path.Combine(directory, "sub", "file.txt"), [1]);
        File.WriteAllBytes(Path.Combine(directory, "SUB", "file.txt"), [1]);

        Assert.NotNull(DirectInstallCheck.Refusal(directory, ValidManifest()));
    }

    [Fact]
    public void MoreThanTheEntryLimitRefuses()
    {
        string directory = WriteValidPlugin("too-many-files");
        for (int index = 0; index < 2_001; index++)
        {
            File.WriteAllBytes(Path.Combine(directory, $"extra-{index}.txt"), []);
        }

        string? refusal = DirectInstallCheck.Refusal(directory, ValidManifest());

        Assert.NotNull(refusal);
        Assert.Contains("2000", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOversizedFileRefuses()
    {
        string directory = WriteValidPlugin("oversized-file");
        File.WriteAllBytes(Path.Combine(directory, "big.txt"), new byte[64 * 1024 * 1024 + 1]);

        Assert.NotNull(DirectInstallCheck.Refusal(directory, ValidManifest()));
    }

    [Fact]
    public void ARuntimesFolderRefuses()
    {
        string directory = WriteValidPlugin("runtimes-folder");
        Directory.CreateDirectory(Path.Combine(directory, "runtimes", "win-x64"));
        File.WriteAllBytes(Path.Combine(directory, "runtimes", "win-x64", "native.dll"), [1]);

        string? refusal = DirectInstallCheck.Refusal(directory, ValidManifest());

        Assert.NotNull(refusal);
        Assert.Contains("runtimes", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisallowedExtensionRefuses()
    {
        string directory = WriteValidPlugin("disallowed-extension");
        File.WriteAllBytes(Path.Combine(directory, "notes.exe"), [1]);

        Assert.NotNull(DirectInstallCheck.Refusal(directory, ValidManifest()));
    }

    [Fact]
    public void AMissingEntryDllRefuses()
    {
        string directory = Path.Combine(_root, "missing-entry-dll");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "plugin.json"), []);

        string? refusal = DirectInstallCheck.Refusal(directory, ValidManifest());

        Assert.NotNull(refusal);
    }

    [Fact]
    public void AValidateForInstallFailureRefuses()
    {
        string directory = WriteValidPlugin("no-hosts");
        var manifest = new LauncherPluginManifest(
            Id,
            Id,
            "0.1.0",
            EntryDll,
            1,
            [LauncherPluginKind.Gameplay],
            new LauncherPluginHostVersion(0, 1, 0),
            null,
            [],
            Hosts: null);

        string? refusal = DirectInstallCheck.Refusal(directory, manifest);

        Assert.NotNull(refusal);
        Assert.Contains("hosts", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidIconRefuses()
    {
        string directory = WriteValidPlugin("bad-icon");
        File.WriteAllBytes(
            Path.Combine(directory, LauncherPluginIcon.FileName),
            PngTestData.WrongDimensions());

        Assert.NotNull(DirectInstallCheck.Refusal(directory, ValidManifest()));
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public void AnUnreadableSubfolderRefuses()
    {
        if (!LauncherOperatingSystem.IsUnix)
        {
            throw new PlatformNotSupportedException("Lane=Unix requires a native Unix host.");
        }

        string directory = WriteValidPlugin("unreadable-subfolder");
        string subfolder = Path.Combine(directory, "locked");
        Directory.CreateDirectory(subfolder);
        File.WriteAllBytes(Path.Combine(subfolder, "file.txt"), [1]);
        File.SetUnixFileMode(subfolder, UnixFileMode.None);
        try
        {
            Assert.NotNull(DirectInstallCheck.Refusal(directory, ValidManifest()));
        }
        finally
        {
            File.SetUnixFileMode(
                subfolder,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static LauncherPluginManifest ValidManifest() =>
        new(
            Id,
            Id,
            "0.1.0",
            EntryDll,
            1,
            [LauncherPluginKind.Gameplay],
            new LauncherPluginHostVersion(0, 1, 0),
            null,
            [],
            [LauncherPluginHostKind.Headless]);

    private string WriteValidPlugin(string folderName)
    {
        string directory = Path.Combine(_root, folderName);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "plugin.json"), []);
        File.WriteAllBytes(Path.Combine(directory, EntryDll), [1, 2, 3]);
        return directory;
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not create the test junction.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Could not create the test junction: "
                + process.StandardError.ReadToEnd()
                + process.StandardOutput.ReadToEnd());
        }
    }

    private static void RunMkfifo(string path)
    {
        using Process process = Process.Start(new ProcessStartInfo("mkfifo")
        {
            ArgumentList = { path },
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start mkfifo.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Could not create a FIFO: " + process.StandardError.ReadToEnd());
        }
    }
}
