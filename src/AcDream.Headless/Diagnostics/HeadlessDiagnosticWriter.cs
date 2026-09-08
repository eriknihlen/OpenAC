using System.Diagnostics;
using System.Text.Json;
using AcDream.Headless.Hosting;
using AcDream.Runtime;

namespace AcDream.Headless.Diagnostics;

internal sealed class HeadlessDiagnosticWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly TextWriter _output;

    internal HeadlessDiagnosticWriter(TextWriter output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    internal void Lifecycle(
        string sessionId,
        string state,
        GameRuntime? runtime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        using Process process = Process.GetCurrentProcess();
        RuntimeStateCheckpoint? checkpoint =
            runtime?.CaptureCheckpoint();
        Write(new
        {
            kind = "lifecycle",
            sessionId,
            state,
            generation = checkpoint?.Generation.Value ?? 0UL,
            lifecycle = checkpoint?.Lifecycle.ToString() ?? "none",
            entities = checkpoint?.EntityCount ?? 0,
            inventoryObjects =
                checkpoint?.InventoryObjectCount ?? 0,
            workingSetBytes = process.WorkingSet64,
            privateBytes = process.PrivateMemorySize64,
        });
    }

    internal void Failure(
        string sessionId,
        string phase,
        Exception error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(error);
        Write(new
        {
            kind = "failure",
            sessionId,
            phase,
            errorType = error.GetType().FullName,
            errorDetail = error.ToString(),
        });
    }

    internal void Message(
        string sessionId,
        string eventName,
        ulong generation = 0UL) =>
        Write(new
        {
            kind = "event",
            sessionId,
            eventName,
            generation,
        });

    internal void Resources(
        string state,
        in HeadlessProcessResourceSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        HeadlessProcessContentSnapshot? content = snapshot.Content;
        HeadlessProcessResourceEnvelopeSnapshot envelope =
            snapshot.Envelope;
        Write(new
        {
            kind = "resources",
            state,
            sequence = snapshot.Sequence,
            monotonicTimestamp = snapshot.MonotonicTimestamp,
            process = snapshot.Process,
            managed = snapshot.Managed,
            sessions = snapshot.Sessions,
            scheduler = new
            {
                snapshot.Scheduler.SessionCount,
                snapshot.Scheduler.WaitCount,
                snapshot.Scheduler.TurnCount,
                snapshot.Scheduler.CatchUpCollapseCount,
                snapshot.Scheduler.LateDeadlineCount,
                snapshot.Scheduler.MeanLatenessMilliseconds,
                snapshot.Scheduler.MaximumLatenessMilliseconds,
                snapshot.Scheduler.ActiveSessionCount,
                snapshot.Scheduler.FaultedSessionCount,
                snapshot.Scheduler.NextDeadline,
            },
            content = content is null
                ? null
                : new
                {
                    content.Value.LeaseCount,
                    content.Value.IsDisposeRequested,
                    content.Value.IsDisposed,
                    content.Value.MappedVirtualBytes,
                    content.Value.IsConverged,
                },
            resourceEnvelope = new
            {
                envelope.Ceilings.Profile,
                envelope.HasRateSample,
                envelope.WithinCeilings,
                formula = new
                {
                    envelope.Ceilings.SharedPrivateBytes,
                    envelope.Ceilings.PrivateBytesPerSession,
                    envelope.Ceilings.SharedWorkingSetBytes,
                    envelope.Ceilings.WorkingSetBytesPerSession,
                    envelope.Ceilings.SharedManagedLiveBytes,
                    envelope.Ceilings.ManagedLiveBytesPerSession,
                    envelope.Ceilings.SharedManagedHeapBytes,
                    envelope.Ceilings.ManagedHeapBytesPerSession,
                    envelope.Ceilings.SharedCpuCorePercent,
                    envelope.Ceilings.CpuCorePercentPerSession,
                    envelope.Ceilings.SharedHandleCount,
                    envelope.Ceilings.HandleCountPerSession,
                    envelope.Ceilings.SharedDescriptorCount,
                    envelope.Ceilings.DescriptorCountPerSession,
                    envelope.Ceilings.SharedSocketCount,
                    envelope.Ceilings.SocketCountPerSession,
                },
                limits = new
                {
                    envelope.MaximumPrivateBytes,
                    envelope.MaximumWorkingSetBytes,
                    envelope.MaximumManagedLiveBytes,
                    envelope.MaximumManagedHeapBytes,
                    envelope.MaximumCpuCorePercent,
                    envelope.MaximumThreadCount,
                    envelope.MaximumHandleCount,
                    envelope.MaximumDescriptorCount,
                    envelope.MaximumSocketCount,
                    envelope.MaximumWaitsPerSecond,
                    envelope.MaximumCatchUpsPerSecond,
                    envelope.MaximumMeanLatenessMilliseconds,
                    envelope.MaximumLatenessMilliseconds,
                },
                observedRates = new
                {
                    envelope.WaitsPerSecond,
                    envelope.TurnsPerSecond,
                    envelope.CatchUpsPerSecond,
                },
                envelope.Violations,
            },
        });
    }

    private void Write<T>(T value)
    {
        _output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        _output.Flush();
    }
}
