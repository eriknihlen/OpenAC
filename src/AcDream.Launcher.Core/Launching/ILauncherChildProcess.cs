using System.Diagnostics;
using System.Runtime.InteropServices;
using AcDream.Launcher.Core;

namespace AcDream.Launcher.Core.Launching;

public interface ILauncherChildProcess : IDisposable
{
    bool HasExited { get; }

    int ExitCode { get; }

    TextWriter StandardInput { get; }

    event EventHandler? Exited;

    void Start();

    bool TryRequestGracefulStop();

    bool CloseMainWindow();

    void Kill();

    bool WaitForExit(TimeSpan timeout);
}

public interface ILauncherChildProcessFactory
{
    ILauncherChildProcess Create(LauncherProcessSpec spec);
}

/// <summary>Real-process implementation used in production.</summary>
public sealed class SystemChildProcessFactory : ILauncherChildProcessFactory
{
    public ILauncherChildProcess Create(LauncherProcessSpec spec) =>
        OperatingSystem.IsWindows() && spec.SupportsConsoleGracefulStop
            ? new WindowsSystemChildProcess(spec)
            : new SystemChildProcess(spec);
}

internal sealed partial class SystemChildProcess : ILauncherChildProcess
{
    private const int Sigint = 2;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int LinuxKill(int pid, int sig);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "kill", SetLastError = true)]
    private static partial int MacKill(int pid, int sig);

    private readonly Process _process;
    private readonly bool _supportsConsoleGracefulStop;
    private readonly BoundedProcessOutputCapture? _stderrCapture;
    private bool _raisingEnabled;
    private bool _errorReadingEnabled;

    internal SystemChildProcess(LauncherProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _supportsConsoleGracefulStop = spec.SupportsConsoleGracefulStop;

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(spec.StderrLogPath))
        {
            startInfo.RedirectStandardError = true;
            _stderrCapture = new BoundedProcessOutputCapture(spec.StderrLogPath);
        }

        foreach (string argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(spec.WorkingDirectory))
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        }

        _process = new Process { StartInfo = startInfo };
    }

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public TextWriter StandardInput => _process.StandardInput;

    public event EventHandler? Exited;

    public void Start()
    {
        _process.EnableRaisingEvents = true;
        _process.Exited += OnExited;
        _raisingEnabled = true;
        if (_stderrCapture is not null)
        {
            _process.ErrorDataReceived += OnErrorDataReceived;
            _errorReadingEnabled = true;
        }

        _process.Start();
        if (_errorReadingEnabled)
        {
            _process.BeginErrorReadLine();
        }
    }

    public bool TryRequestGracefulStop()
    {
        if (!LauncherOperatingSystem.IsUnix || !_supportsConsoleGracefulStop)
        {
            return false;
        }

        try
        {
            return (OperatingSystem.IsMacOS()
                ? MacKill(_process.Id, Sigint)
                : LinuxKill(_process.Id, Sigint)) == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool CloseMainWindow() => _process.CloseMainWindow();

    public void Kill() => _process.Kill(entireProcessTree: true);

    public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);

    public void Dispose()
    {
        if (_raisingEnabled)
        {
            _process.Exited -= OnExited;
        }

        if (_errorReadingEnabled)
        {
            _process.ErrorDataReceived -= OnErrorDataReceived;
        }

        _process.Dispose();
        _stderrCapture?.Dispose();
    }

    private void OnExited(object? sender, EventArgs e) =>
        Exited?.Invoke(this, EventArgs.Empty);

    private void OnErrorDataReceived(object? sender, DataReceivedEventArgs e) =>
        _stderrCapture?.AppendLine(e.Data);
}
