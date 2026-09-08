namespace AcDream.Launcher.Core.Installation;

/// <summary>
/// Versioned machine-readable output from <c>acdream-bake
/// --progress-json</c>. Human output shares stdout but remains a distinct
/// event so the installer never derives state by scraping prose.
/// </summary>
public abstract record BakeProgressEvent(int Version, string EventName);

public sealed record BakeStartedEvent(
    int Version,
    uint BakeToolVersion,
    string? OutputPath)
    : BakeProgressEvent(Version, "started");

public sealed record BakeWorkProgressEvent(
    int Version,
    string Phase,
    long Completed,
    long Total,
    int Failures,
    double ElapsedSeconds,
    double EtaSeconds)
    : BakeProgressEvent(Version, "progress");

public sealed record BakeCompletedEvent(
    int Version,
    uint BakeToolVersion,
    long OutputBytes,
    int Failures)
    : BakeProgressEvent(Version, "completed");

public sealed record BakeErrorEvent(int Version, string Message)
    : BakeProgressEvent(Version, "error");

public sealed record UnknownBakeProgressEvent(
    int Version,
    string EventName,
    string RawLine)
    : BakeProgressEvent(Version, EventName);

public sealed record FutureBakeProgressEvent(
    int Version,
    string EventName,
    string RawLine)
    : BakeProgressEvent(Version, EventName);

public sealed record MalformedBakeProgressEvent(
    string RawLine,
    string Reason)
    : BakeProgressEvent(0, "malformed");

public sealed record BakeHumanOutputEvent(string Text)
    : BakeProgressEvent(0, "human");
