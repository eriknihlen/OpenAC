namespace AcDream.Launcher.Core.Launching;

public sealed record LauncherProcessSpec(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    bool SupportsConsoleGracefulStop = true,
    string? StderrLogPath = null);
