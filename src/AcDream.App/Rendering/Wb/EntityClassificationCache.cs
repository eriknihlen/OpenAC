using System.Collections.Generic;

namespace AcDream.App.Rendering.Wb;

internal sealed class EntityClassificationCache
{
    private readonly Dictionary<(uint EntityId, uint LandblockHint), EntityCacheEntry> _entries = new();

    public int Count => _entries.Count;

    public bool TryGet(uint entityId, uint landblockHint, out EntityCacheEntry? entry)
        => _entries.TryGetValue((entityId, landblockHint), out entry);

    public void Populate(
        uint entityId,
        uint landblockHint,
        CachedBatch[] batches,
        CachedSelectionPart[]? selectionParts = null)
    {
        _entries[(entityId, landblockHint)] = new EntityCacheEntry
        {
            EntityId = entityId,
            LandblockHint = landblockHint,
            Batches = batches,
            SelectionParts = selectionParts ?? [],
        };
    }

    public void InvalidateEntity(uint entityId)
    {
        if (_entries.Count == 0) return;
        List<(uint, uint)>? toRemove = null;
        foreach (var key in _entries.Keys)
        {
            if (key.EntityId == entityId)
            {
                toRemove ??= new List<(uint, uint)>();
                toRemove.Add(key);
            }
        }
        if (toRemove is null) return;
        foreach (var k in toRemove) _entries.Remove(k);
    }

    public void InvalidateLandblock(uint landblockId)
    {
        if (_entries.Count == 0) return;

        List<(uint, uint)>? toRemove = null;
        foreach (var key in _entries.Keys)
        {
            if (key.LandblockHint == landblockId)
            {
                toRemove ??= new List<(uint, uint)>();
                toRemove.Add(key);
            }
        }
        if (toRemove is null) return;
        foreach (var k in toRemove) _entries.Remove(k);
    }

#if DEBUG
    /// <summary>
    /// Asserts that the cached entry for <paramref name="entityId"/> still
    /// matches what fresh classification would produce. Catches the prior
    /// Tier 1 bug class — silent caching of mutable per-frame state — by
    /// firing <see cref="System.Diagnostics.Debug.Assert"/> when any cached
    /// field has drifted from live state.
    ///
    /// <para>
    /// Caller passes per-batch live state (Key, TextureSlot, RestPose)
    /// reconstructed from the same path the populate ran. The cache iterates
    /// its stored entries in parallel and asserts equality.
    /// </para>
    ///
    /// <para>
    /// As of Phase 4 (commit f16604b) this method is exercised by unit tests
    /// only; the dispatcher's cache-hit branch fires a simpler predicate assert
    /// (<c>!isAnimated</c>) at production hit time. Wiring the full live-state
    /// cross-check into the per-entity branch is the spec section 6.5 stretch
    /// goal and remains open as a follow-up. Zero cost in Release; the method
    /// stays here so the regression-class guard is locked behind tests.
    /// </para>
    /// </summary>
    public void DebugCrossCheck(uint entityId, uint landblockHint, IReadOnlyList<CachedBatch> liveBatches)
    {
        if (!_entries.TryGetValue((entityId, landblockHint), out var entry)) return;

        System.Diagnostics.Debug.Assert(
            entry.Batches.Length == liveBatches.Count,
            $"EntityClassificationCache: batch count mismatch for entity {entityId}: cached={entry.Batches.Length} live={liveBatches.Count}");

        for (int i = 0; i < entry.Batches.Length && i < liveBatches.Count; i++)
        {
            var cached = entry.Batches[i];
            var live = liveBatches[i];
            System.Diagnostics.Debug.Assert(
                cached.Key.Equals(live.Key),
                $"EntityClassificationCache: GroupKey drift for entity {entityId} batch {i}");
            System.Diagnostics.Debug.Assert(
                cached.TextureSlot == live.TextureSlot,
                $"EntityClassificationCache: texture slot drift for entity {entityId} batch {i}");
            System.Diagnostics.Debug.Assert(
                MatrixApproxEqual(cached.RestPose, live.RestPose, epsilon: 1e-5f),
                $"EntityClassificationCache: RestPose drift for entity {entityId} batch {i}");
        }
    }

    private static bool MatrixApproxEqual(System.Numerics.Matrix4x4 a, System.Numerics.Matrix4x4 b, float epsilon)
    {
        return System.MathF.Abs(a.M11 - b.M11) <= epsilon && System.MathF.Abs(a.M12 - b.M12) <= epsilon &&
               System.MathF.Abs(a.M13 - b.M13) <= epsilon && System.MathF.Abs(a.M14 - b.M14) <= epsilon &&
               System.MathF.Abs(a.M21 - b.M21) <= epsilon && System.MathF.Abs(a.M22 - b.M22) <= epsilon &&
               System.MathF.Abs(a.M23 - b.M23) <= epsilon && System.MathF.Abs(a.M24 - b.M24) <= epsilon &&
               System.MathF.Abs(a.M31 - b.M31) <= epsilon && System.MathF.Abs(a.M32 - b.M32) <= epsilon &&
               System.MathF.Abs(a.M33 - b.M33) <= epsilon && System.MathF.Abs(a.M34 - b.M34) <= epsilon &&
               System.MathF.Abs(a.M41 - b.M41) <= epsilon && System.MathF.Abs(a.M42 - b.M42) <= epsilon &&
               System.MathF.Abs(a.M43 - b.M43) <= epsilon && System.MathF.Abs(a.M44 - b.M44) <= epsilon;
    }
#endif
}
