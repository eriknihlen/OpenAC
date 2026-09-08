using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AcDream.Core.Rendering;

namespace AcDream.App.Diagnostics;

/// <summary>Stage indices for per-frame CPU attribution.</summary>
public enum FrameStage
{
    /// <summary>Whole OnUpdate body (simulation + streaming apply).</summary>
    Update = 0,
    /// <summary>WbMeshAdapter.Tick — staged mesh/texture GPU upload drain.</summary>
    Upload = 1,
    /// <summary>ImGui Render (dev overlay).</summary>
    ImGui = 2,
    /// <summary>Software presentation deadline wait (zero under VSync or uncapped mode).</summary>
    Pacing = 3,
}

internal readonly record struct FrameHistoryRecord(
    int FrameIndex,
    double TimestampMs,
    long CpuUs,
    long GpuUs,
    long AllocBytes,
    long UpdateUs,
    long UploadUs,
    long ImGuiUs,
    long PacingUs);

public sealed class FrameProfiler : IDisposable
{
    private const int WindowCapacity = 2048;              // ~12 s at 165 fps
    private const int HistoryInitialCapacity = 131072;
    private const long ReportIntervalTicks = 5 * TimeSpan.TicksPerSecond;
    private static readonly int StageCount = Enum.GetValues<FrameStage>().Length;

    private readonly FrameStatsBuffer _cpuUs = new(WindowCapacity);
    private readonly FrameStatsBuffer _gpuUs = new(WindowCapacity);
    private readonly FrameStatsBuffer _allocBytes = new(WindowCapacity);
    private readonly FrameStatsBuffer[] _stageUs;
    private readonly long[] _stageAccumTicks;
    private readonly long[] _lastStageUs;
    private readonly List<FrameHistoryRecord>? _history;
    private readonly long _profilerStartTimestamp;
    private readonly DateTime _profilerStartUtc;
    private int _currentFrameIndex = -1;

    private bool _externalGpuActive;
    private long _lastBoundaryTimestamp;
    private long _lastAllocBytes;
    private long _lastReportTicks;
    private int _gc0Base, _gc1Base, _gc2Base;
    private int _framesInWindow;
    private int _ownerThreadId;
    private bool _threadWarned;
    private bool _wasEnabled;

    public string? LastReport { get; private set; }

    public int CurrentFrameIndex => _currentFrameIndex;

    public FrameProfiler()
    {
        _stageUs = new FrameStatsBuffer[StageCount];
        for (int i = 0; i < StageCount; i++) _stageUs[i] = new FrameStatsBuffer(WindowCapacity);
        _stageAccumTicks = new long[StageCount];
        _lastStageUs = new long[StageCount];
        _profilerStartTimestamp = Stopwatch.GetTimestamp();
        _profilerStartUtc = DateTime.UtcNow;
        if (RenderingDiagnostics.FrameHistoryPath is not null)
            _history = new List<FrameHistoryRecord>(HistoryInitialCapacity);
    }

    public void FrameBoundary()
    {
        bool enabled = RenderingDiagnostics.FrameProfEnabled;
        if (!enabled)
        {
            if (_wasEnabled)
            {
                _wasEnabled = false;
                _lastBoundaryTimestamp = 0;
                _currentFrameIndex = -1;
            }
            return;
        }

        if (_ownerThreadId == 0) _ownerThreadId = Environment.CurrentManagedThreadId;
        else if (!_threadWarned && _ownerThreadId != Environment.CurrentManagedThreadId)
        {
            _threadWarned = true;
            Console.WriteLine("[frame-prof] WARNING: frame boundary crossed threads; alloc counter is per-thread and now unreliable");
        }

        long now = Stopwatch.GetTimestamp();
        long allocNow = GC.GetAllocatedBytesForCurrentThread();

        if (!_wasEnabled)
        {
            _wasEnabled = true;
            _lastReportTicks = DateTime.UtcNow.Ticks;
            Array.Clear(_stageAccumTicks);
            _gc0Base = GC.CollectionCount(0); _gc1Base = GC.CollectionCount(1); _gc2Base = GC.CollectionCount(2);
            _currentFrameIndex = 0;
        }
        else
        {
            long cpuUs = (now - _lastBoundaryTimestamp) * 1_000_000L / Stopwatch.Frequency;
            _cpuUs.Push(cpuUs);
            long allocDelta = allocNow - _lastAllocBytes;
            _allocBytes.Push(allocDelta);
            for (int i = 0; i < StageCount; i++)
            {
                long stageUs = _stageAccumTicks[i] * 1_000_000L / Stopwatch.Frequency;
                _stageUs[i].Push(stageUs);
                _lastStageUs[i] = stageUs;
                _stageAccumTicks[i] = 0;
            }
            _framesInWindow++;
            if (_history is not null)
            {
                _history.Add(new FrameHistoryRecord(
                    _currentFrameIndex,
                    (now - _profilerStartTimestamp) * 1000.0 / Stopwatch.Frequency,
                    cpuUs,
                    -1L,
                    allocDelta,
                    _lastStageUs[(int)FrameStage.Update],
                    _lastStageUs[(int)FrameStage.Upload],
                    _lastStageUs[(int)FrameStage.ImGui],
                    _lastStageUs[(int)FrameStage.Pacing]));
            }
            _currentFrameIndex++;
        }

        _lastBoundaryTimestamp = now;
        _lastAllocBytes = allocNow;

        long nowTicks = DateTime.UtcNow.Ticks;
        if (nowTicks - _lastReportTicks >= ReportIntervalTicks && _framesInWindow > 0)
        {
            int gc0 = GC.CollectionCount(0) - _gc0Base;
            int gc1 = GC.CollectionCount(1) - _gc1Base;
            int gc2 = GC.CollectionCount(2) - _gc2Base;
            LastReport = FormatReport(_framesInWindow, _cpuUs, _gpuUs,
                gpuActive: _externalGpuActive,
                _allocBytes, gc0, gc1, gc2, _stageUs);
            Console.WriteLine(LastReport);
            _lastReportTicks = nowTicks;
            _gc0Base += gc0; _gc1Base += gc1; _gc2Base += gc2;
            _framesInWindow = 0;
            _cpuUs.Reset(); _gpuUs.Reset(); _allocBytes.Reset();
            for (int i = 0; i < StageCount; i++) _stageUs[i].Reset();
        }
    }

    public void RecordGpuSample(int frameIndex, long elapsedUs)
    {
        if (!_wasEnabled)
            return;

        _externalGpuActive = true;
        _gpuUs.Push(elapsedUs);
        if (_history is not null && (uint)frameIndex < (uint)_history.Count)
        {
            FrameHistoryRecord row = _history[frameIndex];
            _history[frameIndex] = row with { GpuUs = elapsedUs };
        }
    }

    public StageScope BeginStage(FrameStage stage)
        => RenderingDiagnostics.FrameProfEnabled
            ? new StageScope(this, stage, Stopwatch.GetTimestamp())
            : default;

    internal void EndStage(FrameStage stage, long startTimestamp)
        => _stageAccumTicks[(int)stage] += Stopwatch.GetTimestamp() - startTimestamp;

    public static string FormatReport(
        int frameCount,
        FrameStatsBuffer cpu, FrameStatsBuffer gpu, bool gpuActive,
        FrameStatsBuffer alloc, int gc0, int gc1, int gc2,
        FrameStatsBuffer[] stages)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256);
        sb.Append("[frame-prof] n=").Append(frameCount);
        sb.AppendFormat(ci, " | cpu_ms p50={0:0.0} p95={1:0.0} p99={2:0.0} max={3:0.0}",
            cpu.Percentile(0.50) / 1000.0, cpu.Percentile(0.95) / 1000.0,
            cpu.Percentile(0.99) / 1000.0, cpu.Max() / 1000.0);
        if (gpuActive)
            sb.AppendFormat(ci, " | gpu_ms p50={0:0.0} p95={1:0.0}",
                gpu.Percentile(0.50) / 1000.0, gpu.Percentile(0.95) / 1000.0);
        else
            sb.Append(" | gpu=off(wbdiag)");
        sb.AppendFormat(ci, " | alloc_kb p50={0:0.0} max={1:0.0} gc={2}/{3}/{4}",
            alloc.Percentile(0.50) / 1024.0, alloc.Max() / 1024.0, gc0, gc1, gc2);
        string[] names = { "upd", "upl", "imgui", "pace" };
        for (int i = 0; i < stages.Length && i < names.Length; i++)
            sb.AppendFormat(ci, " | {0} p50={1:0.0} p95={2:0.0}",
                names[i], stages[i].Percentile(0.50) / 1000.0, stages[i].Percentile(0.95) / 1000.0);
        return sb.ToString();
    }

    internal static void WriteHistoryCsv(
        IEnumerable<FrameHistoryRecord> records,
        TextWriter writer,
        DateTime profilerStartUtc)
    {
        var ci = CultureInfo.InvariantCulture;
        DateTime startUtc = profilerStartUtc.ToUniversalTime();
        writer.WriteLine(
            "frame,timestamp_ms,timestamp_utc,cpu_us,gpu_us,alloc_bytes,"
            + "update_us,upload_us,imgui_us,pacing_us");
        foreach (FrameHistoryRecord r in records)
        {
            writer.Write(r.FrameIndex.ToString(ci)); writer.Write(',');
            writer.Write(r.TimestampMs.ToString("0.000", ci)); writer.Write(',');
            writer.Write(startUtc.AddMilliseconds(r.TimestampMs).ToString("O", ci));
            writer.Write(',');
            writer.Write(r.CpuUs.ToString(ci)); writer.Write(',');
            writer.Write(r.GpuUs.ToString(ci)); writer.Write(',');
            writer.Write(r.AllocBytes.ToString(ci)); writer.Write(',');
            writer.Write(r.UpdateUs.ToString(ci)); writer.Write(',');
            writer.Write(r.UploadUs.ToString(ci)); writer.Write(',');
            writer.Write(r.ImGuiUs.ToString(ci)); writer.Write(',');
            writer.WriteLine(r.PacingUs.ToString(ci));
        }
    }

    public void Dispose()
    {
        if (_history is { Count: > 0 } && RenderingDiagnostics.FrameHistoryPath is { } path)
        {
            try
            {
                using var writer = new StreamWriter(path, append: false);
                WriteHistoryCsv(_history, writer, _profilerStartUtc);
                Console.WriteLine($"[frame-prof] wrote {_history.Count} history record(s) to '{path}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[frame-prof] WARNING: failed to write frame history to '{path}': {ex.Message}");
            }
        }
    }
}

/// <summary>Disposable stage scope; default instance is a no-op.</summary>
public readonly struct StageScope : IDisposable
{
    private readonly FrameProfiler? _owner;
    private readonly FrameStage _stage;
    private readonly long _start;

    internal StageScope(FrameProfiler owner, FrameStage stage, long start)
    {
        _owner = owner; _stage = stage; _start = start;
    }

    public void Dispose() => _owner?.EndStage(_stage, _start);
}
