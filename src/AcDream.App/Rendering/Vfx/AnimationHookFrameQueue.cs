using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using DatReaderWriter.Types;

namespace AcDream.App.Rendering.Vfx;

public sealed class AnimationHookFrameQueue
{
    private readonly AnimationHookRouter _router;
    private readonly IEntityEffectPoseSource _poses;
    private readonly IEntityEffectPoseLifetimeSource? _poseLifetimes;
    private readonly List<Entry> _entries = new();

    public AnimationHookFrameQueue(
        AnimationHookRouter router,
        IEntityEffectPoseSource poses)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _poses = poses ?? throw new ArgumentNullException(nameof(poses));
        _poseLifetimes = poses as IEntityEffectPoseLifetimeSource;
    }

    public int Count => _entries.Count;

    public void Capture(uint ownerLocalId, AnimationSequencer sequencer)
    {
        ArgumentNullException.ThrowIfNull(sequencer);
        Capture(ownerLocalId, sequencer, sequencer.ConsumePendingHooks());
    }

    internal void Capture(
        uint ownerLocalId,
        AnimationSequencer sequencer,
        IReadOnlyList<AnimationHook> hooks)
    {
        ArgumentNullException.ThrowIfNull(sequencer);
        ArgumentNullException.ThrowIfNull(hooks);
        ulong ownerLifetimeVersion =
            _poseLifetimes?.GetPoseOwnerLifetimeVersion(ownerLocalId) ?? 0UL;

        for (int i = 0; i < hooks.Count; i++)
        {
            if (_poseLifetimes is not null
                && _poseLifetimes.GetPoseOwnerLifetimeVersion(
                    ownerLocalId) != ownerLifetimeVersion)
            {
                break;
            }
            if (hooks[i] is AnimationDoneHook)
                sequencer.Manager.AnimationDone(success: true);
        }

        if (hooks.Count == 0)
            return;

        _entries.Add(new Entry(
            ownerLocalId,
            ownerLifetimeVersion,
            hooks));
    }

    public void Drain()
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            if (_poseLifetimes is not null
                && _poseLifetimes.GetPoseOwnerLifetimeVersion(
                    entry.OwnerLocalId) != entry.OwnerLifetimeVersion)
            {
                continue;
            }
            for (int hi = 0; hi < entry.Hooks.Count; hi++)
            {
                // A prior hook sink can tear down and replace this local ID.
                // Revalidate per hook so the remainder of the old PES/animation
                // batch can never spill into the replacement incarnation.
                if (_poseLifetimes is not null
                    && _poseLifetimes.GetPoseOwnerLifetimeVersion(
                        entry.OwnerLocalId) != entry.OwnerLifetimeVersion)
                {
                    break;
                }
                AnimationHook? hook = entry.Hooks[hi];
                if (hook is null)
                    continue;
                if (_poses.TryGetRootPose(
                        entry.OwnerLocalId,
                        out Matrix4x4 rootWorld))
                {
                    _router.OnHook(
                        entry.OwnerLocalId,
                        rootWorld.Translation,
                        hook);
                }
            }
        }
        _entries.Clear();
    }

    public void Clear() => _entries.Clear();

    private readonly record struct Entry(
        uint OwnerLocalId,
        ulong OwnerLifetimeVersion,
        IReadOnlyList<AnimationHook> Hooks);
}
