using System.Runtime.InteropServices;
using System.Text.Json;
using AcDream.Launcher.Core.Launching;

if (args.Length != 3)
{
    return 64;
}

string resultPath = Path.GetFullPath(args[0]);
string dotnetPath = args[1];
string childAssembly = Path.GetFullPath(args[2]);
string root = Path.Combine(
    Path.GetDirectoryName(resultPath)!,
    "consoleless-children");
Directory.CreateDirectory(root);
string targetReady = Path.Combine(root, "target.ready.json");
string targetBreak = Path.Combine(root, "target.break");
string siblingReady = Path.Combine(root, "sibling.ready.json");
string siblingBreak = Path.Combine(root, "sibling.break");

bool parentHadConsoleBefore = HasConsole();
using var target = new LauncherProcessSupervisor();
using var sibling = new LauncherProcessSupervisor();
try
{
    target.Start(
        Spec(dotnetPath, childAssembly, targetReady, targetBreak, "target"),
        "stdin-from-consoleless-parent");
    sibling.Start(
        Spec(dotnetPath, childAssembly, siblingReady, siblingBreak, "sibling"),
        password: null);
    WaitForFile(targetReady, target);
    WaitForFile(siblingReady, sibling);
    bool parentHadConsoleAfterStarts = HasConsole();

    target.Stop(TimeSpan.FromSeconds(10));
    bool siblingUnaffected = sibling.State != LauncherSessionState.Exited
        && !File.Exists(siblingBreak);
    sibling.Stop(TimeSpan.FromSeconds(10));

    Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
    File.WriteAllText(
        resultPath,
        JsonSerializer.Serialize(new
        {
            parentHadConsoleBefore,
            parentHadConsoleAfterStarts,
            targetExitCode = target.ExitCode,
            siblingExitCode = sibling.ExitCode,
            targetBreakObserved = File.Exists(targetBreak),
            siblingBreakObserved = File.Exists(siblingBreak),
            siblingUnaffected,
        }));
    return 0;
}
catch (Exception error)
{
    Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
    File.WriteAllText(
        resultPath,
        JsonSerializer.Serialize(new
        {
            parentHadConsoleBefore,
            error = error.GetType().Name + ": " + error.Message,
        }));
    return 1;
}
finally
{
    ForceStop(target);
    ForceStop(sibling);
}

static LauncherProcessSpec Spec(
    string dotnetPath,
    string childAssembly,
    string ready,
    string breakMarker,
    string label) =>
    new(
        dotnetPath,
        [
            childAssembly,
            "wait-for-break",
            ready,
            breakMarker,
            label,
            "argument with spaces",
        ]);

static void WaitForFile(string path, LauncherProcessSupervisor supervisor)
{
    DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (!File.Exists(path))
    {
        if (supervisor.State == LauncherSessionState.Exited)
        {
            throw new InvalidOperationException(
                $"Child exited early with {supervisor.ExitCode}.");
        }

        if (DateTime.UtcNow >= deadline)
        {
            throw new TimeoutException("Child did not become ready.");
        }

        Thread.Sleep(20);
    }
}

static void ForceStop(LauncherProcessSupervisor supervisor)
{
    try
    {
        supervisor.Stop(TimeSpan.Zero);
    }
    catch
    {
    }
}

static bool HasConsole()
{
    uint[] processes = new uint[1];
    return Native.GetConsoleProcessList(processes, 1) != 0;
}

internal static partial class Native
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetConsoleProcessList(
        [Out] uint[] processList,
        uint processCount);
}
