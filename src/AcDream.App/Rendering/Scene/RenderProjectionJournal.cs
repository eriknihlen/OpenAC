namespace AcDream.App.Rendering.Scene;

internal sealed class RenderProjectionJournal
{
    private readonly List<RenderProjectionDelta> _deltas = [];
    private ulong _nextSequence = 1;

    public RenderProjectionJournal(RenderSceneGeneration generation)
    {
        Generation = generation;
    }

    public RenderSceneGeneration Generation { get; private set; }
    public int Count => _deltas.Count;
    public int Capacity => _deltas.Capacity;

    public void Register(in RenderProjectionRecord record) =>
        _deltas.Add(RenderProjectionDelta.Register(
            Generation,
            NextSequence(),
            in record));

    public void Update(
        RenderProjectionDeltaKind kind,
        in RenderProjectionRecord record) =>
        _deltas.Add(RenderProjectionDelta.Update(
            kind,
            Generation,
            NextSequence(),
            in record));

    public void Unregister(
        RenderProjectionId id,
        RenderOwnerIncarnation incarnation) =>
        _deltas.Add(RenderProjectionDelta.Unregister(
            Generation,
            NextSequence(),
            id,
            incarnation));

    public void AppendDifference(
        in RenderProjectionRecord prior,
        in RenderProjectionRecord current)
    {
        if (prior.Id != current.Id
            || prior.OwnerIncarnation != current.OwnerIncarnation
            || prior.ProjectionClass != current.ProjectionClass)
        {
            throw new ArgumentException(
                "A channel update cannot change projection identity, incarnation, or class.",
                nameof(current));
        }

        if (prior.Transform != current.Transform
            || prior.Bounds != current.Bounds
            || prior.SortKey != current.SortKey
            || prior.Source.TransformFingerprint
                != current.Source.TransformFingerprint)
        {
            Update(RenderProjectionDeltaKind.UpdateTransform, in current);
        }

        if (prior.MeshSet != current.MeshSet
            || prior.Material != current.Material
            || prior.DegradeState != current.DegradeState
            || prior.EntityPayload != current.EntityPayload
            || prior.Source.GeometryFingerprint
                != current.Source.GeometryFingerprint
            || prior.Source.AppearanceFingerprint
                != current.Source.AppearanceFingerprint)
        {
            Update(RenderProjectionDeltaKind.UpdateAppearance, in current);
        }

        if (prior.Residency != current.Residency)
            Update(RenderProjectionDeltaKind.Rebucket, in current);
        if (prior.Flags != current.Flags)
            Update(RenderProjectionDeltaKind.UpdateFlags, in current);
    }

    public RenderDeltaApplyResult DrainTo(IRenderScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Generation != Generation)
        {
            throw new InvalidOperationException(
                $"Journal {Generation} cannot drain into scene {scene.Generation}.");
        }

        RenderDeltaApplyResult result =
            scene.Apply(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_deltas));
        _deltas.Clear();
        return result;
    }

    public void Clear(RenderSceneGeneration replacementGeneration)
    {
        if (replacementGeneration.CompareTo(Generation) <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(replacementGeneration),
                replacementGeneration,
                "A replacement journal generation must advance.");
        }

        _deltas.Clear();
        Generation = replacementGeneration;
    }

    internal ReadOnlySpan<RenderProjectionDelta> Pending =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_deltas);

    private ulong NextSequence()
    {
        if (_nextSequence == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "Render projection journal sequence exhausted.");
        }

        return _nextSequence++;
    }
}
