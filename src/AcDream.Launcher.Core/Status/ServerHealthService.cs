using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace AcDream.Launcher.Core.Status;

public sealed class ServerHealthService(
    HttpClient httpClient,
    IServerReachabilityProbe probe,
    TimeProvider? timeProvider = null) : IServerHealthService
{
    private static readonly Uri PopulationUri = new("https://treestats.net/player_counts-latest.json");
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _populationGate = new(1, 1);
    private Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastAttempt;
    private DateTimeOffset? _lastSuccess;

    public async Task<ServerHealthSnapshot> CheckAsync(string host, int port, string serverName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        Task<double?> reachability = CheckReachabilityAsync(host, port, cancellationToken);
        var populationTask = GetPopulationAsync(serverName, cancellationToken);
        await Task.WhenAll(reachability, populationTask).ConfigureAwait(false);
        var population = await populationTask.ConfigureAwait(false);
        double? latency = await reachability.ConfigureAwait(false);
        return new(latency.HasValue ? true : null, latency, population.Count,
            population.Stale, _time.GetUtcNow());
    }

    private async Task<double?> CheckReachabilityAsync(string host, int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await probe.ProbeAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
        catch (IOException) { return null; }
    }

    private async Task<(int? Count, bool Stale)> GetPopulationAsync(string serverName, CancellationToken token)
    {
        await _populationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_lastAttempt is null || now - _lastAttempt >= TimeSpan.FromMinutes(1))
            {
                _lastAttempt = now;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    using var response = await httpClient.GetAsync(PopulationUri,
                        HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await response.Content.LoadIntoBufferAsync(1_048_576, timeout.Token).ConfigureAwait(false);
                    _counts = ParsePlayerCounts(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                    _lastSuccess = _time.GetUtcNow();
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (HttpRequestException) { }
                catch (JsonException) { }
                catch (IOException) { }
            }
            int? count = _counts.TryGetValue(serverName.Trim(), out var value) ? value : null;
            bool stale = count.HasValue && (_lastSuccess is null || _lastAttempt > _lastSuccess
                || _time.GetUtcNow() - _lastSuccess > TimeSpan.FromMinutes(5));
            return (count, stale);
        }
        finally { _populationGate.Release(); }
    }

    internal static Dictionary<string, int> ParsePlayerCounts(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a population list.");
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("server", out var name) || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString())
                || !item.TryGetProperty("count", out var count) || count.ValueKind != JsonValueKind.Number
                || !count.TryGetInt32(out var value) || value < 0)
                continue;
            counts[name.GetString()!.Trim()] = value;
        }
        return counts;
    }
}
