using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using AcDream.App.Rendering.Residency;

namespace AcDream.App.Rendering;

internal enum RetailAlphaList : byte
{
    Alpha = 0,
    Clip = 1,
}

internal enum RetailAlphaFlushSite
{
    DrawBuilding,
    SortCellExit,
    LandscapeFlush,
    RenderNormalMode,
}

internal interface IRetailAlphaDrawSource
{
    void PrepareAlphaDraws(ReadOnlySpan<int> tokens);

    void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount);

    void ResetAlphaSubmissions();
}

internal readonly record struct RetailAlphaEntry(
    IRetailAlphaDrawSource Source,
    int Token,
    bool OverrideClipmap);

internal interface IWorldSceneAlphaFrame
{
    void BeginFrame();

    void EndFrame();

    void AbortFrame();
}

internal sealed class RetailAlphaQueue : IWorldSceneAlphaFrame
{
    internal const int ListCapacity = 3000;

    private const int MinimumSubmissionCapacity = 256;
    private const int SubmissionGrowthQuantum = 256;
    private const int MinimumSourceCapacity = 4;

    private readonly List<RetailAlphaEntry> _clip = new(256);
    private readonly List<RetailAlphaEntry> _alpha = new(256);
    private readonly List<IRetailAlphaDrawSource> _sources = new(4);
    private int[] _tokenScratch = new int[256];
    private int[] _sourceDrawOffsets = new int[4];
    private readonly RetainedScratchCapacityPolicy _scratchPolicy;
    private readonly long _scratchBudgetBytes;
    private readonly Action<RetailAlphaFlushSite>? _drainObserver;

    internal RetailAlphaQueue(
        long? scratchBudgetBytes = null,
        Action<RetailAlphaFlushSite>? drainObserver = null)
    {
        long budget = scratchBudgetBytes
            ?? AlphaScratchBudgetProfile.Create(
                ResidencyBudgetOptions.Default.AlphaScratchBytes).QueueBytes;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        _scratchBudgetBytes = budget;
        _scratchPolicy = new RetainedScratchCapacityPolicy(budget);
        _drainObserver = drainObserver;
    }

    public bool IsCollecting { get; private set; }

    internal int ClipCount => _clip.Count;

    internal int AlphaCount => _alpha.Count;

    /// <summary>Total pending entries across both lists.</summary>
    internal int PendingCount => _clip.Count + _alpha.Count;

    internal long ScratchBudgetBytes => _scratchBudgetBytes;

    internal long RetainedScratchBytes => checked(
        (long)_clip.Capacity * Unsafe.SizeOf<RetailAlphaEntry>()
        + (long)_alpha.Capacity * Unsafe.SizeOf<RetailAlphaEntry>()
        + (long)_tokenScratch.Length * sizeof(int)
        + (long)_sources.Capacity * IntPtr.Size
        + (long)_sourceDrawOffsets.Length * sizeof(int));

    public void BeginFrame()
    {
        if (IsCollecting)
            throw new InvalidOperationException("Retail alpha frame is already active.");
        if (_clip.Count != 0 || _alpha.Count != 0 || _sources.Count != 0)
            throw new InvalidOperationException("Retail alpha queue retained payload outside a frame.");

        IsCollecting = true;
    }

    internal bool TryAppend(
        RetailAlphaList list,
        IRetailAlphaDrawSource source,
        int token,
        bool overrideClipmap)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsCollecting)
            throw new InvalidOperationException("Retail alpha submission requires an active frame.");
        if (token < 0)
            throw new ArgumentOutOfRangeException(nameof(token));

        List<RetailAlphaEntry> target = list == RetailAlphaList.Clip ? _clip : _alpha;
        RegisterSource(source);
        if (target.Count >= ListCapacity)
            return false;

        target.Add(new RetailAlphaEntry(source, token, overrideClipmap));
        return true;
    }

    private void RegisterSource(IRetailAlphaDrawSource source)
    {
        for (int i = 0; i < _sources.Count; i++)
            if (ReferenceEquals(_sources[i], source))
                return;
        _sources.Add(source);
    }

    public void Flush(RetailAlphaFlushSite site, float threshold)
    {
        if (!IsCollecting)
            throw new InvalidOperationException("Retail alpha flush requires an active frame.");

        if (_clip.Count < threshold * ListCapacity && _alpha.Count < threshold * ListCapacity)
            return;

        _drainObserver?.Invoke(site);
        DrainAndReset();
    }

    private void DrainAndReset()
    {
        Exception? drawFailure = null;
        List<Exception>? resetFailures = null;
        try
        {
            int total = _clip.Count + _alpha.Count;
            if (total > 0)
            {
                EnsureTokenCapacity(total);
                EnsureSourceCapacity(_sources.Count);
                Array.Clear(_sourceDrawOffsets, 0, _sources.Count);

                for (int sourceIndex = 0; sourceIndex < _sources.Count; sourceIndex++)
                {
                    IRetailAlphaDrawSource source = _sources[sourceIndex];
                    int sourceCount = 0;
                    for (int i = 0; i < total; i++)
                    {
                        RetailAlphaEntry entry = Entry(i);
                        if (ReferenceEquals(entry.Source, source))
                            _tokenScratch[sourceCount++] = entry.Token;
                    }

                    if (sourceCount > 0)
                        source.PrepareAlphaDraws(_tokenScratch.AsSpan(0, sourceCount));
                }

                int start = 0;
                while (start < total)
                {
                    IRetailAlphaDrawSource source = Entry(start).Source;
                    int end = start + 1;
                    while (end < total && ReferenceEquals(Entry(end).Source, source))
                        end++;

                    int count = end - start;
                    int sourceIndex = FindSourceIndex(source);
                    int firstPreparedDraw = _sourceDrawOffsets[sourceIndex];
                    source.DrawPreparedAlphaBatch(firstPreparedDraw, count);
                    _sourceDrawOffsets[sourceIndex] += count;
                    start = end;
                }
            }
        }
        catch (Exception error)
        {
            drawFailure = error;
        }
        finally
        {
            int observedClip = _clip.Count;
            int observedAlpha = _alpha.Count;
            int observedSources = _sources.Count;
            for (int i = 0; i < _sources.Count; i++)
            {
                try
                {
                    _sources[i].ResetAlphaSubmissions();
                }
                catch (Exception error)
                {
                    (resetFailures ??= []).Add(error);
                }
            }
            _sources.Clear();
            _clip.Clear();
            _alpha.Clear();
            ApplyScratchRetention(observedClip + observedAlpha, observedSources);
        }

        if (drawFailure is not null)
        {
            if (resetFailures is { Count: > 0 })
            {
                resetFailures.Insert(0, drawFailure);
                throw new AggregateException(
                    "Retail alpha drawing failed and its submissions could not be fully reset.",
                    resetFailures);
            }

            ExceptionDispatchInfo.Capture(drawFailure).Throw();
        }

        if (resetFailures is { Count: > 0 })
        {
            throw new AggregateException(
                "Retail alpha submissions could not be fully reset.",
                resetFailures);
        }
    }

    private RetailAlphaEntry Entry(int index) =>
        index < _clip.Count ? _clip[index] : _alpha[index - _clip.Count];

    public void EndFrame()
    {
        if (!IsCollecting)
            throw new InvalidOperationException("Retail alpha frame is not active.");

        try
        {
            Flush(RetailAlphaFlushSite.RenderNormalMode, 0f);
        }
        finally
        {
            IsCollecting = false;
        }
    }

    /// <summary>
    /// Discards an incomplete frame without drawing it. Every source is still
    /// told to release its retained submission payload so the next frame begins
    /// from the same empty invariant as a successful flush.
    /// </summary>
    public void AbortFrame()
    {
        List<Exception>? failures = null;
        try
        {
            for (int i = 0; i < _sources.Count; i++)
            {
                try
                {
                    _sources[i].ResetAlphaSubmissions();
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }
        }
        finally
        {
            int observedClip = _clip.Count;
            int observedAlpha = _alpha.Count;
            int observedSources = _sources.Count;
            _sources.Clear();
            _clip.Clear();
            _alpha.Clear();
            IsCollecting = false;
            ApplyScratchRetention(observedClip + observedAlpha, observedSources);
        }

        if (failures is { Count: > 0 })
            throw new AggregateException("Retail alpha frame abort failed.", failures);
    }

    private void EnsureTokenCapacity(int count)
    {
        if (_tokenScratch.Length >= count)
            return;
        Array.Resize(ref _tokenScratch, count + 256);
    }

    private void EnsureSourceCapacity(int count)
    {
        if (_sourceDrawOffsets.Length >= count)
            return;
        Array.Resize(ref _sourceDrawOffsets, count + 4);
    }

    private int FindSourceIndex(IRetailAlphaDrawSource source)
    {
        for (int i = 0; i < _sources.Count; i++)
            if (ReferenceEquals(_sources[i], source))
                return i;
        throw new InvalidOperationException("Retail alpha source was not registered for this frame.");
    }

    private void ApplyScratchRetention(
        int observedEntryCount,
        int observedSourceCount)
    {
        int currentCapacity = Math.Max(
            Math.Max(_clip.Capacity, _alpha.Capacity),
            _tokenScratch.Length);
        int bytesPerEntry =
            checked(
                2 * Unsafe.SizeOf<RetailAlphaEntry>()
                + sizeof(int)
                + IntPtr.Size);
        int targetCapacity = _scratchPolicy.ObserveAndSelectCapacity(
            currentCapacity,
            observedEntryCount,
            bytesPerEntry,
            MinimumSubmissionCapacity,
            SubmissionGrowthQuantum);
        if (targetCapacity < currentCapacity)
        {
            _clip.Capacity = targetCapacity;
            _alpha.Capacity = targetCapacity;
            Array.Resize(ref _tokenScratch, targetCapacity);

            int sourceTarget = Math.Max(
                MinimumSourceCapacity,
                observedSourceCount == 0
                    ? MinimumSourceCapacity
                    : checked(observedSourceCount * 2));
            sourceTarget = Math.Min(sourceTarget, _sources.Capacity);
            _sources.Capacity = sourceTarget;
            if (_sourceDrawOffsets.Length > sourceTarget)
                Array.Resize(ref _sourceDrawOffsets, sourceTarget);
        }
    }
}
