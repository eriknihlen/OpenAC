namespace AcDream.Launcher.Core.Status;

public sealed record ServerHealthSnapshot(
    bool? IsReachable,
    double? LatencyMilliseconds,
    int? PlayerCount,
    bool IsPlayerCountStale,
    DateTimeOffset CheckedAt);

public interface IServerHealthService
{
    Task<ServerHealthSnapshot> CheckAsync(string host, int port, string serverName,
        CancellationToken cancellationToken = default);
}

public interface IServerReachabilityProbe
{
    Task<double?> ProbeAsync(string host, int port, CancellationToken cancellationToken);
}
