using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Audio;
using DatReaderWriter.DBObjs;
using DRWSound = DatReaderWriter.Enums.Sound;

namespace AcDream.App.Audio;

public sealed class AmbientSoundController
{
    private readonly OpenAlAudioEngine _engine;
    private readonly DatSoundCache _cache;
    private readonly AmbientSoundScheduler _scheduler;
    private readonly AmbientSoundGatherer _gatherer;
    private readonly ISoundRandom _rng;
    private readonly List<AmbientSoundFiring> _firings = [];

    private Region? _region;
    private Func<uint, ushort[]?> _landblocks = static _ => null;
    private uint _currentObjCell;
    private Vector3 _listenerPosition;
    private double _clock;
    private bool _suspended;

    public AmbientSoundController(
        OpenAlAudioEngine engine,
        DatSoundCache cache,
        ISoundRandom? rng = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _rng = rng ?? new SoundRandom();
        _scheduler = new AmbientSoundScheduler(_rng);
        _gatherer = new AmbientSoundGatherer(_scheduler);
    }

    public int InstanceCount => _scheduler.Instances.Count;

    /// <summary>Instances holding a deadline. Diagnostic use.</summary>
    public int QueuedCount => _scheduler.QueuedCount;

    /// <summary>
    /// Install the region whose authored ambient data drives the soundscape, and
    /// the terrain-word source for the 3×3 ring.
    /// </summary>
    public void InstallRegion(Region region, Func<uint, ushort[]?> landblocks)
    {
        _region = region ?? throw new ArgumentNullException(nameof(region));
        _landblocks = landblocks ?? throw new ArgumentNullException(nameof(landblocks));
        _currentObjCell = 0;
        _scheduler.Clear();
    }

    public void ObserveListener(
        uint objCellId,
        Vector3 position,
        Vector3 landblockLocalPosition,
        bool seenOutside = false)
    {
        _listenerPosition = position;
        if (_region is null || objCellId == _currentObjCell)
            return;

        _currentObjCell = objCellId;

        if (IsIndoorCell(objCellId) && !seenOutside)
        {
            _scheduler.Clear();
            return;
        }

        _gatherer.Rebuild(
            _region,
            (objCellId >> 16 << 16) | 0xFFFFu,
            landblockLocalPosition,
            _landblocks,
            _clock);
    }

    public void Tick(double deltaSeconds)
    {
        if (deltaSeconds > 0)
            _clock += deltaSeconds;

        if (_suspended || !_engine.IsAvailable || _region is null)
            return;

        _firings.Clear();
        _scheduler.Tick(_clock, _firings, _listenerPosition);
        Emit();
    }

    public void Suspend()
    {
        _suspended = true;
        StopAll();
    }

    public void Resume() => _suspended = false;

    /// <summary>Drop every instance and deadline (world teardown / reset).</summary>
    public void StopAll()
    {
        _scheduler.Clear();
        _currentObjCell = 0;
    }

    private void Emit()
    {
        foreach (AmbientSoundFiring firing in _firings)
            Play(firing);
        _firings.Clear();
    }

    private void Play(in AmbientSoundFiring firing)
    {
        SoundTable? table = _cache.GetSoundTable(firing.Instance.SoundTableDid);
        if (table is null)
            return;

        // The variant pick and the entry's probability gate apply to ambients
        // exactly as they do everywhere else — PlayAmbientSound* rolls the same
        // PlayProbability inline.
        var entry = SoundCookbook.Select(
            table,
            (DRWSound)(uint)firing.Instance.Descriptor.Sound,
            _rng);
        if (entry is null)
            return;

        uint waveId = (uint)entry.Id;
        if (waveId == 0)
            return;

        WaveData? wave = _cache.GetWave(waveId);
        if (wave is null)
            return;

        float volume = firing.Volume * _engine.AmbientVolume;

        if (firing.Position is { } position)
        {
            _engine.PlayAmbient3DWave(waveId, wave, position, volume, entry.Priority);
            return;
        }

        _engine.PlayAmbientFromCenter(waveId, wave, volume, entry.Priority);
    }

    private static bool IsIndoorCell(uint objCellId) => (objCellId & 0xFFFFu) >= 0x0100u;
}

public interface IAmbientFramePhase
{
    void TickAmbient(float deltaSeconds);
}

public sealed class AmbientFramePhase : IAmbientFramePhase
{
    private readonly AmbientSoundController _ambient;
    private readonly IAmbientListenerSource _listener;

    public AmbientFramePhase(AmbientSoundController ambient, IAmbientListenerSource listener)
    {
        _ambient = ambient ?? throw new ArgumentNullException(nameof(ambient));
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
    }

    public void TickAmbient(float deltaSeconds)
    {
        if (_listener.TryGetListener(out AmbientListenerPose pose))
        {
            _ambient.ObserveListener(
                pose.ObjCellId,
                pose.Position,
                pose.LandblockLocalPosition,
                pose.SeenOutside);
        }
        _ambient.Tick(deltaSeconds);
    }
}

public readonly record struct AmbientListenerPose(
    uint ObjCellId,
    Vector3 Position,
    Vector3 LandblockLocalPosition,
    bool SeenOutside);

public interface IAmbientListenerSource
{
    bool TryGetListener(out AmbientListenerPose pose);
}

public sealed class LocalPlayerAmbientListenerSource : IAmbientListenerSource
{
    private readonly AcDream.Runtime.Gameplay.RuntimeLocalPlayerMovementState _player;
    private readonly Func<uint, Vector3, Vector3?> _indoorLandblockLocal;

    public LocalPlayerAmbientListenerSource(
        AcDream.Runtime.Gameplay.RuntimeLocalPlayerMovementState player,
        Func<uint, Vector3, Vector3?>? indoorLandblockLocal = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _indoorLandblockLocal = indoorLandblockLocal ?? ((_, _) => null);
    }

    public bool TryGetListener(out AmbientListenerPose pose)
    {
        if (_player.Controller is { } controller)
        {
            AcDream.Core.Physics.Position cell = controller.CellPosition;
            uint objCellId = controller.CellId;
            Vector3 landblockLocal = cell.Frame.Origin;
            bool seenOutside = false;

            if ((objCellId & 0xFFFFu) >= 0x0100u)
            {
                if (_indoorLandblockLocal(objCellId, cell.Frame.Origin)
                    is { } converted)
                {
                    landblockLocal = converted;
                    seenOutside = true;
                }
            }

            pose = new AmbientListenerPose(
                objCellId,
                controller.Position,
                landblockLocal,
                seenOutside);
            return true;
        }

        pose = default;
        return false;
    }
}
