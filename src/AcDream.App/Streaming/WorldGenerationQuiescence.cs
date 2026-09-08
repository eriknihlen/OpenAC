using AcDream.App.Audio;
using AcDream.Core.Selection;
using AcDream.Runtime.Entities;
using AcDream.Runtime.World;

namespace AcDream.App.Streaming;

/// <summary>
/// Read-only generation gate shared by world simulation and presentation.
/// A hard login/portal reveal boundary makes the prior world unavailable
/// immediately while its physical owners retire over later frames.
/// </summary>
public interface IWorldGenerationAvailability
{
    bool IsWorldAvailable { get; }
    long QuiescedGeneration { get; }
}

internal sealed class AlwaysAvailableWorldGeneration
    : IWorldGenerationAvailability
{
    public static AlwaysAvailableWorldGeneration Instance { get; } = new();
    public bool IsWorldAvailable => true;
    public long QuiescedGeneration => 0;
}

internal sealed class WorldGenerationAvailabilityState
    : IWorldGenerationAvailability
{
    private readonly RuntimeWorldTransitState _transit;

    public WorldGenerationAvailabilityState(
        RuntimeWorldTransitState transit)
    {
        _transit = transit ?? throw new ArgumentNullException(nameof(transit));
    }

    public bool IsWorldAvailable =>
        _transit.IsWorldSimulationAvailable;

    public long QuiescedGeneration =>
        IsWorldAvailable ? 0 : _transit.Snapshot.Generation;
}

internal readonly record struct WorldGenerationQuiescenceEdge(
    bool ShouldApply,
    bool ClearWorldSelection);

internal sealed class WorldGenerationQuiescence
{
    private readonly SelectionState _selection;
    private readonly GpuWorldState _world;
    private readonly Func<uint, RuntimeEntityKey?> _resolveProjectionKey;
    private readonly IWorldAudioQuiescence? _audio;
    private bool _effectsQuiesced;
    private bool _selectionEdgeCommitted;
    private bool _audioSuspended;
    private bool _audioTransitionActive;

    public WorldGenerationQuiescence(
        SelectionState selection,
        GpuWorldState world,
        Func<uint, RuntimeEntityKey?> resolveProjectionKey,
        IWorldAudioQuiescence? audio)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _resolveProjectionKey = resolveProjectionKey
            ?? throw new ArgumentNullException(nameof(resolveProjectionKey));
        _audio = audio;
    }

    public WorldGenerationQuiescenceEdge CaptureBegin()
    {
        if (_effectsQuiesced)
            return default;

        uint? selected = _selection.SelectedObjectId;
        bool clearWorldSelection =
            selected is uint selectedGuid
            && _resolveProjectionKey(selectedGuid) is RuntimeEntityKey key
            && _world.IsLiveEntityVisible(key);
        return new WorldGenerationQuiescenceEdge(
            ShouldApply: true,
            clearWorldSelection);
    }

    public void CommitBegin(in WorldGenerationQuiescenceEdge edge)
    {
        if (!edge.ShouldApply)
            return;

        _effectsQuiesced = true;
        if (!_selectionEdgeCommitted)
        {
            _selectionEdgeCommitted = true;
            if (edge.ClearWorldSelection)
            {
                _selection.Clear(
                    SelectionChangeSource.System,
                    SelectionChangeReason.Cleared);
            }
        }

        ReconcileAudio();
    }

    public void ObserveReleased()
    {
        if (!_effectsQuiesced
            && !_selectionEdgeCommitted
            && !_audioSuspended)
        {
            return;
        }

        _effectsQuiesced = false;
        ReconcileAudio();
        if (!_effectsQuiesced && !_audioSuspended)
            _selectionEdgeCommitted = false;
    }

    private void ReconcileAudio()
    {
        if (_audio is null || _audioTransitionActive)
            return;

        _audioTransitionActive = true;
        try
        {
            while (_audioSuspended != _effectsQuiesced)
            {
                bool suspend = _effectsQuiesced;
                if (suspend)
                    _audio.SuspendWorldAudio();
                else
                    _audio.ResumeWorldAudio();
                _audioSuspended = suspend;
            }
        }
        finally
        {
            _audioTransitionActive = false;
        }
    }
}
