using System.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Runtime;

namespace AcDream.Headless.Diagnostics;

internal readonly record struct HeadlessProcessUsageSnapshot(
    long WorkingSetBytes,
    long PrivateBytes,
    long VirtualBytes,
    long TotalProcessorTimeTicks,
    double SampleIntervalMilliseconds,
    double CpuCorePercent,
    double CpuMachinePercent,
    int ProcessorCount,
    int ThreadCount,
    int HandleCount,
    int FileDescriptorCount,
    int SocketDescriptorCount);

internal readonly record struct HeadlessManagedUsageSnapshot(
    long LiveBytes,
    long HeapSizeBytes,
    long FragmentedBytes,
    long CommittedBytes,
    long TotalAllocatedBytes,
    long MemoryLoadBytes,
    long HighMemoryLoadThresholdBytes,
    double PauseTimePercentage,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

internal readonly record struct HeadlessSessionUsageSnapshot(
    int ConfiguredCount,
    int InWorldCount,
    int FaultedCount,
    int PendingReconnectCount,
    int ConvergedRuntimeCount,
    int EntityCount,
    int InventoryObjectCount,
    int HostLeaseCount);

internal readonly record struct HeadlessProcessResourceSnapshot(
    long Sequence,
    long MonotonicTimestamp,
    HeadlessProcessUsageSnapshot Process,
    HeadlessManagedUsageSnapshot Managed,
    HeadlessSessionUsageSnapshot Sessions,
    HeadlessSchedulerSnapshot Scheduler,
    HeadlessProcessContentSnapshot? Content,
    HeadlessProcessResourceEnvelopeSnapshot Envelope);

internal sealed class HeadlessProcessResourceSampler : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly Process _process;
    private readonly HeadlessProcessResourceEnvelopeEvaluator _envelope;
    private long _sequence;
    private long _previousTimestamp;
    private long _previousProcessorTicks;
    private bool _hasPreviousSample;
    private bool _disposed;

    internal HeadlessProcessResourceSampler(
        TimeProvider? timeProvider = null,
        Process? process = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _process = process ?? Process.GetCurrentProcess();
        _envelope = new HeadlessProcessResourceEnvelopeEvaluator();
    }

    internal HeadlessProcessResourceSnapshot Capture(
        string state,
        IReadOnlyList<HeadlessSessionHost> sessions,
        HeadlessSchedulerSnapshot scheduler,
        HeadlessProcessContentSnapshot? content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentNullException.ThrowIfNull(sessions);

        long now = _timeProvider.GetTimestamp();
        _process.Refresh();
        long processorTicks = _process.TotalProcessorTime.Ticks;
        double intervalMilliseconds = 0d;
        double cpuCorePercent = 0d;
        double cpuMachinePercent = 0d;
        if (_hasPreviousSample && now > _previousTimestamp)
        {
            double intervalSeconds = _timeProvider
                .GetElapsedTime(_previousTimestamp, now)
                .TotalSeconds;
            long processorDelta = Math.Max(
                0L,
                processorTicks - _previousProcessorTicks);
            if (intervalSeconds > 0d)
            {
                intervalMilliseconds = intervalSeconds * 1000d;
                cpuCorePercent =
                    processorDelta
                    * 100d
                    / TimeSpan.TicksPerSecond
                    / intervalSeconds;
                cpuMachinePercent =
                    cpuCorePercent
                    / Math.Max(1, Environment.ProcessorCount);
            }
        }
        _previousTimestamp = now;
        _previousProcessorTicks = processorTicks;
        _hasPreviousSample = true;

        int inWorldCount = 0;
        int faultedCount = 0;
        int pendingReconnectCount = 0;
        int convergedRuntimeCount = 0;
        int entityCount = 0;
        int inventoryObjectCount = 0;
        int hostLeaseCount = 0;
        for (int index = 0; index < sessions.Count; index++)
        {
            HeadlessSessionHost session = sessions[index];
            RuntimeStateCheckpoint checkpoint =
                session.Runtime.CaptureCheckpoint();
            GameRuntimeOwnershipSnapshot ownership =
                session.Runtime.CaptureOwnership();
            if (session.Runtime.Session.IsInWorld)
                inWorldCount++;
            if (session.IsFaulted)
                faultedCount++;
            if (session.IsReconnectPending)
                pendingReconnectCount++;
            if (ownership.IsConverged)
                convergedRuntimeCount++;
            entityCount = checked(entityCount + checkpoint.EntityCount);
            inventoryObjectCount = checked(
                inventoryObjectCount
                + checkpoint.InventoryObjectCount);
            hostLeaseCount = checked(
                hostLeaseCount + ownership.HostLeaseCount);
        }

        GCMemoryInfo memory = GC.GetGCMemoryInfo(GCKind.Any);
        CaptureLinuxDescriptors(
            out int fileDescriptorCount,
            out int socketDescriptorCount);
        var process = new HeadlessProcessUsageSnapshot(
            _process.WorkingSet64,
            _process.PrivateMemorySize64,
            _process.VirtualMemorySize64,
            processorTicks,
            intervalMilliseconds,
            cpuCorePercent,
            cpuMachinePercent,
            Environment.ProcessorCount,
            TryGetThreadCount(_process),
            TryGetHandleCount(_process),
            fileDescriptorCount,
            socketDescriptorCount);
        var managed = new HeadlessManagedUsageSnapshot(
            GC.GetTotalMemory(forceFullCollection: false),
            memory.HeapSizeBytes,
            memory.FragmentedBytes,
            memory.TotalCommittedBytes,
            GC.GetTotalAllocatedBytes(precise: false),
            memory.MemoryLoadBytes,
            memory.HighMemoryLoadThresholdBytes,
            memory.PauseTimePercentage,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
        var sessionUsage = new HeadlessSessionUsageSnapshot(
            sessions.Count,
            inWorldCount,
            faultedCount,
            pendingReconnectCount,
            convergedRuntimeCount,
            entityCount,
            inventoryObjectCount,
            hostLeaseCount);
        HeadlessProcessResourceEnvelopeSnapshot envelope =
            _envelope.Evaluate(
                state,
                process,
                managed,
                sessionUsage,
                scheduler,
                content);
        return new HeadlessProcessResourceSnapshot(
            checked(++_sequence),
            now,
            process,
            managed,
            sessionUsage,
            scheduler,
            content,
            envelope);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _process.Dispose();
        _disposed = true;
    }

    private static int TryGetThreadCount(Process process)
    {
        try
        {
            return process.Threads.Count;
        }
        catch (PlatformNotSupportedException)
        {
            return -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static int TryGetHandleCount(Process process)
    {
        try
        {
            return process.HandleCount;
        }
        catch (PlatformNotSupportedException)
        {
            return -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void CaptureLinuxDescriptors(
        out int fileDescriptorCount,
        out int socketDescriptorCount)
    {
        fileDescriptorCount = -1;
        socketDescriptorCount = -1;
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            int descriptors = 0;
            int sockets = 0;
            foreach (string path
                in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
            {
                descriptors++;
                string? target = new FileInfo(path).LinkTarget;
                if (target?.StartsWith(
                        "socket:[",
                        StringComparison.Ordinal) == true)
                {
                    sockets++;
                }
            }
            fileDescriptorCount = descriptors;
            socketDescriptorCount = sockets;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
