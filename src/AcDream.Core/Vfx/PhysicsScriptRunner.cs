using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Types;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;

namespace AcDream.Core.Vfx;

public sealed class PhysicsScriptRunner
{
    public const float ImmediateCallPesThresholdSeconds = 0.0002f;

    private readonly Func<uint, DatPhysicsScript?> _resolver;
    private readonly IAnimationHookSink _sink;
    private readonly Func<double> _clock;
    private readonly Func<double> _randomUnit;
    private readonly Func<uint, bool> _canAdvanceOwner;
    private readonly Dictionary<uint, DatPhysicsScript?> _scriptCache = new();
    private readonly Dictionary<uint, OwnerQueue> _owners = new();
    private readonly Dictionary<uint, Vector3> _ownerAnchors = new();
    private readonly List<DelayedCallPes> _delayedCalls = new();
    private readonly List<uint> _ownerIterationSnapshot = new();
    private double _gameTime;
    private long _tickGeneration;
    private bool _frameTimePublished;
    private bool _processingTimedHooks;
    private DispatchContext? _dispatchContext;

    public PhysicsScriptRunner(
        Func<uint, DatPhysicsScript?> resolver,
        IAnimationHookSink sink,
        Func<double>? clock = null,
        Func<double>? randomUnit = null,
        Func<uint, bool>? canAdvanceOwner = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _clock = clock ?? (() => _gameTime);
        _randomUnit = randomUnit ?? Random.Shared.NextDouble;
        _canAdvanceOwner = canAdvanceOwner ?? (_ => true);
    }

    /// <summary>Queued PhysicsScripts across every owner, including tails.</summary>
    public int ActiveScriptCount => _owners.Values.Sum(owner => owner.Scripts.Count);

    public int ActiveOwnerCount => _owners.Count;
    public int ScheduledCallPesCount => _delayedCalls.Count;
    public int OwnerAnchorCount => _ownerAnchors.Count;

    public bool DiagEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_DUMP_PLAYSCRIPT") == "1";

    /// <summary>Always-on structural/load diagnostics; production supplies a logger.</summary>
    public Action<string>? DiagnosticSink { get; set; }

    /// <summary>Updates the root anchor used when this owner's hooks dispatch.</summary>
    public void SetOwnerAnchor(uint ownerLocalId, Vector3 worldPosition)
    {
        if (ownerLocalId != 0)
            _ownerAnchors[ownerLocalId] = worldPosition;
    }

    /// <summary>Enqueues a direct F754/Setup PhysicsScript DID.</summary>
    public bool PlayDirect(uint ownerLocalId, uint scriptDid)
    {
        if (ownerLocalId == 0 || scriptDid == 0)
            return false;

        DatPhysicsScript? script = ResolveScript(scriptDid);
        if (script is null || script.ScriptData.Count == 0)
        {
            ReportDiagnostic(
                $"PhysicsScript 0x{scriptDid:X8} for owner 0x{ownerLocalId:X8} is missing or empty.");
            if (DiagEnabled)
                Console.WriteLine($"[pes] missing/empty script=0x{scriptDid:X8} owner=0x{ownerLocalId:X8}");
            return false;
        }
        if (script.ScriptData.Any(entry => !double.IsFinite(entry.StartTime)))
        {
            ReportDiagnostic(
                $"PhysicsScript 0x{scriptDid:X8} for owner 0x{ownerLocalId:X8} has a non-finite hook time.");
            return false;
        }

        if (!_owners.TryGetValue(ownerLocalId, out OwnerQueue? owner))
        {
            owner = new OwnerQueue();
            _owners.Add(ownerLocalId, owner);
        }

        double start = owner.Scripts.Count == 0
            ? _clock()
            : owner.Scripts[^1].StartTime + owner.Scripts[^1].Duration;
        if (!double.IsFinite(start))
        {
            ReportDiagnostic(
                $"PhysicsScript 0x{scriptDid:X8} for owner 0x{ownerLocalId:X8} produced a non-finite start time.");
            return false;
        }

        HashSet<uint>? immediateAncestors = null;
        if (_dispatchContext is { } dispatch
            && dispatch.OwnerLocalId == ownerLocalId
            && start <= dispatch.Script.StartTime)
        {
            immediateAncestors = dispatch.Script.ImmediateAncestors is null
                ? new HashSet<uint> { dispatch.Script.ScriptDid }
                : new HashSet<uint>(dispatch.Script.ImmediateAncestors) { dispatch.Script.ScriptDid };
            if (immediateAncestors.Contains(scriptDid))
            {
                ReportDiagnostic(
                    $"Rejected zero-time recursive PhysicsScript 0x{scriptDid:X8} for owner " +
                    $"0x{ownerLocalId:X8}; DAT CallPES chain would never yield.");
                return false;
            }
        }

        owner.Scripts.Add(new ScheduledScript(
            scriptDid,
            script,
            start,
            immediateAncestors,
            EarliestScriptTickGeneration(ownerLocalId)));

        if (DiagEnabled)
            Console.WriteLine($"[pes] enqueue script=0x{scriptDid:X8} owner=0x{ownerLocalId:X8} start={start:R} depth={owner.Scripts.Count}");
        return true;
    }

    public bool Play(uint scriptId, uint entityId, Vector3 anchorWorldPos)
    {
        SetOwnerAnchor(entityId, anchorWorldPos);
        return PlayDirect(entityId, scriptId);
    }

    public bool ScheduleCallPes(uint ownerLocalId, uint scriptDid, float maximumPause)
    {
        if (ownerLocalId == 0 || scriptDid == 0 || !float.IsFinite(maximumPause))
            return false;
        if (maximumPause < ImmediateCallPesThresholdSeconds)
            return PlayDirect(ownerLocalId, scriptDid);

        double sample = _randomUnit();
        if (!double.IsFinite(sample))
            return false;
        sample = Math.Clamp(sample, 0.0, 1.0);
        double due = _clock() + sample * maximumPause;
        _delayedCalls.Add(new DelayedCallPes(
            ownerLocalId,
            scriptDid,
            due,
            EarliestTickGenerationForNewTimedHook()));
        return true;
    }

    public void PublishTime(double gameTime)
    {
        ValidateMonotonicTime(gameTime);
        _gameTime = gameTime;
        _frameTimePublished = true;
    }

    public void Tick(double gameTime)
    {
        ValidateMonotonicTime(gameTime);
        _gameTime = gameTime;
        _frameTimePublished = false;
        _tickGeneration++;

        DrainDueCallPes(gameTime);

        _ownerIterationSnapshot.Clear();
        foreach (uint ownerId in _owners.Keys)
            _ownerIterationSnapshot.Add(ownerId);
        for (int ownerIndex = 0; ownerIndex < _ownerIterationSnapshot.Count; ownerIndex++)
        {
            uint ownerId = _ownerIterationSnapshot[ownerIndex];
            if (!_owners.TryGetValue(ownerId, out OwnerQueue? owner))
                continue;
            if (!_canAdvanceOwner(ownerId))
                continue;
            DrainOwner(ownerId, owner, gameTime);
        }
    }

    public void Tick(float deltaSeconds)
    {
        if (deltaSeconds < 0f)
            deltaSeconds = 0f;
        Tick(_gameTime + deltaSeconds);
    }

    public void StopAllForEntity(uint ownerLocalId)
    {
        _owners.Remove(ownerLocalId);
        _ownerAnchors.Remove(ownerLocalId);
        _delayedCalls.RemoveAll(call => call.OwnerLocalId == ownerLocalId);
    }

    public void Clear()
    {
        _owners.Clear();
        _ownerAnchors.Clear();
        _delayedCalls.Clear();
    }

    public void RegisterScriptForTest(uint id, DatPhysicsScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _scriptCache[id] = script;
    }

    private void DrainDueCallPes(double gameTime)
    {
        _processingTimedHooks = true;
        try
        {
            for (int i = _delayedCalls.Count - 1; i >= 0; i--)
            {
                DelayedCallPes call = _delayedCalls[i];
                if (call.DueTime > gameTime
                    || call.EarliestTickGeneration > _tickGeneration
                    || !_canAdvanceOwner(call.OwnerLocalId))
                    continue;
                _delayedCalls.RemoveAt(i);
                PlayDirect(call.OwnerLocalId, call.ScriptDid);
            }
        }
        finally
        {
            _processingTimedHooks = false;
        }
    }

    private void DrainOwner(uint ownerId, OwnerQueue owner, double gameTime)
    {
        while (owner.Scripts.Count > 0)
        {
            ScheduledScript current = owner.Scripts[0];
            if (current.EarliestTickGeneration > _tickGeneration)
                return;
            while (current.NextHookIndex < current.Script.ScriptData.Count)
            {
                PhysicsScriptData entry = current.Script.ScriptData[current.NextHookIndex];
                if (current.StartTime + entry.StartTime > gameTime)
                    break;

                current.NextHookIndex++;
                DispatchHook(ownerId, current, entry.Hook);

                if (!_owners.TryGetValue(ownerId, out OwnerQueue? currentOwner)
                    || !ReferenceEquals(currentOwner, owner))
                    return;
            }

            if (current.NextHookIndex < current.Script.ScriptData.Count)
                return;

            owner.Scripts.RemoveAt(0);
        }

        _owners.Remove(ownerId);
    }

    private void DispatchHook(uint ownerId, ScheduledScript script, AnimationHook hook)
    {
        if (DiagEnabled)
            Console.WriteLine($"[pes] fire script=0x{script.ScriptDid:X8} owner=0x{ownerId:X8} hook={hook.HookType}");

        _ownerAnchors.TryGetValue(ownerId, out Vector3 anchor);
        DispatchContext? previous = _dispatchContext;
        _dispatchContext = new DispatchContext(ownerId, script);
        try
        {
            _sink.OnHook(ownerId, anchor, hook);
        }
        finally
        {
            _dispatchContext = previous;
        }
    }

    private DatPhysicsScript? ResolveScript(uint id)
    {
        if (_scriptCache.TryGetValue(id, out DatPhysicsScript? cached))
            return cached;
        DatPhysicsScript? script;
        try
        {
            script = _resolver(id);
        }
        catch (Exception error)
        {
            ReportDiagnostic($"Failed to load PhysicsScript 0x{id:X8}: {error.Message}");
            if (DiagEnabled)
                Console.WriteLine($"[pes] load failed script=0x{id:X8}: {error.Message}");
            script = null;
        }
        _scriptCache[id] = script;
        return script;
    }

    private long EarliestTickGenerationForNewTimedHook()
    {
        return _frameTimePublished
            ? _tickGeneration + 2
            : _tickGeneration + 1;
    }

    private long EarliestScriptTickGeneration(uint ownerLocalId)
    {
        if (_processingTimedHooks
            || _dispatchContext is { OwnerLocalId: var dispatchOwner }
                && dispatchOwner == ownerLocalId)
            return _tickGeneration;
        return _tickGeneration + 1;
    }

    private void ValidateMonotonicTime(double gameTime)
    {
        if (!double.IsFinite(gameTime))
            throw new ArgumentOutOfRangeException(nameof(gameTime));
        if (gameTime < _gameTime)
            throw new ArgumentOutOfRangeException(nameof(gameTime), "PhysicsScript time must be monotonic.");
    }

    private void ReportDiagnostic(string message) => DiagnosticSink?.Invoke(message);

    private sealed class OwnerQueue
    {
        public List<ScheduledScript> Scripts { get; } = new();
    }

    private sealed class ScheduledScript
    {
        public ScheduledScript(
            uint scriptDid,
            DatPhysicsScript script,
            double startTime,
            HashSet<uint>? immediateAncestors,
            long earliestTickGeneration)
        {
            ScriptDid = scriptDid;
            Script = script;
            StartTime = startTime;
            Duration = script.ScriptData[^1].StartTime;
            ImmediateAncestors = immediateAncestors;
            EarliestTickGeneration = earliestTickGeneration;
        }

        public uint ScriptDid { get; }
        public DatPhysicsScript Script { get; }
        public double StartTime { get; }
        public double Duration { get; }
        public HashSet<uint>? ImmediateAncestors { get; }
        public long EarliestTickGeneration { get; }
        public int NextHookIndex { get; set; }
    }

    private readonly record struct DelayedCallPes(
        uint OwnerLocalId,
        uint ScriptDid,
        double DueTime,
        long EarliestTickGeneration);

    private sealed record DispatchContext(uint OwnerLocalId, ScheduledScript Script);
}
