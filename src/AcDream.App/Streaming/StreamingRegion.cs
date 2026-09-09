using System;
using System.Collections.Generic;
using System.Linq;

namespace AcDream.App.Streaming;

public sealed class StreamingRegion
{
    public int CenterX    { get; private set; }
    public int CenterY    { get; private set; }
    public int Radius     { get; }
    public int NearRadius { get; }
    public int FarRadius  { get; }

    private readonly HashSet<uint> _visible = new();

    private readonly HashSet<uint> _resident = new();

    private readonly Dictionary<uint, TierResidence> _tierResidence = new();

    private bool _bootstrapped;

    public IReadOnlyCollection<uint> Visible => _visible;

    public IReadOnlyCollection<uint> Resident => _resident;

    public StreamingRegion(int centerX, int centerY, int nearRadius, int farRadius)
    {
        NearRadius = nearRadius;
        FarRadius  = farRadius;
        Radius     = farRadius;  // outer ring drives Resident bookkeeping
        Recenter(centerX, centerY);
    }

    public StreamingRegion(int cx, int cy, int radius) : this(cx, cy, radius, radius) { }

    private void Recenter(int cx, int cy)
    {
        CenterX = cx;
        CenterY = cy;
        _visible.Clear();
        for (int dx = -Radius; dx <= Radius; dx++)
        {
            for (int dy = -Radius; dy <= Radius; dy++)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || nx > 0xFF || ny < 0 || ny > 0xFF)
                    continue;
                _visible.Add(EncodeLandblockId(nx, ny));
            }
        }
        _resident.UnionWith(_visible);
    }

    internal static uint EncodeLandblockId(int lbX, int lbY)
        => ((uint)lbX << 24) | ((uint)lbY << 16) | 0xFFFFu;

    public TwoTierDiff ComputeFirstTickDiff()
    {
        var near = new List<uint>();
        var far  = new List<uint>();
        for (int dx = -FarRadius; dx <= FarRadius; dx++)
        {
            for (int dy = -FarRadius; dy <= FarRadius; dy++)
            {
                int nx = CenterX + dx;
                int ny = CenterY + dy;
                if (nx < 0 || nx > 0xFF || ny < 0 || ny > 0xFF) continue;
                int absDx = System.Math.Abs(dx);
                int absDy = System.Math.Abs(dy);
                var id = EncodeLandblockId(nx, ny);
                if (absDx <= NearRadius && absDy <= NearRadius)
                    near.Add(id);
                else
                    far.Add(id);
            }
        }
        return new TwoTierDiff(
            ToLoadFar:  far,
            ToLoadNear: near,
            ToPromote:  System.Array.Empty<uint>(),
            ToDemote:   System.Array.Empty<uint>(),
            ToUnload:   System.Array.Empty<uint>());
    }

    public void MarkResidentFromBootstrap()
    {
        if (_bootstrapped)
            throw new InvalidOperationException(
                "MarkResidentFromBootstrap was already called; calling it again would " +
                "reset accumulated tier-residence state and silently drop differential " +
                "data built up by interim RecenterTo calls.");

        _tierResidence.Clear();
        for (int dx = -FarRadius; dx <= FarRadius; dx++)
        {
            for (int dy = -FarRadius; dy <= FarRadius; dy++)
            {
                int nx = CenterX + dx;
                int ny = CenterY + dy;
                if (nx < 0 || nx > 0xFF || ny < 0 || ny > 0xFF) continue;
                int absDx = Math.Abs(dx);
                int absDy = Math.Abs(dy);
                var id = EncodeLandblockId(nx, ny);
                _tierResidence[id] = (absDx <= NearRadius && absDy <= NearRadius)
                    ? TierResidence.Near
                    : TierResidence.Far;
            }
        }
        _bootstrapped = true;
    }

    internal static uint EncodeLandblockIdForTest(int lbX, int lbY)
        => EncodeLandblockId(lbX, lbY);

    internal bool TryGetDesiredTier(uint landblockId, out LandblockStreamTier tier)
    {
        if (_tierResidence.TryGetValue(landblockId, out var residence))
        {
            tier = residence == TierResidence.Near
                ? LandblockStreamTier.Near
                : LandblockStreamTier.Far;
            return true;
        }

        tier = default;
        return false;
    }

    public TwoTierDiff RecenterTo(int newCx, int newCy)
    {
        if (!_bootstrapped)
            throw new InvalidOperationException(
                "Two-tier RecenterTo called before MarkResidentFromBootstrap. " +
                "First call ComputeFirstTickDiff to enqueue the bootstrap loads, " +
                "then MarkResidentFromBootstrap to seed _tierResidence, then RecenterTo " +
                "for subsequent observer moves.");

        int nearUnloadThreshold = NearRadius + 2;
        int farUnloadThreshold  = FarRadius  + 2;

        var toLoadFar  = new List<uint>();
        var toLoadNear = new List<uint>();
        var toPromote  = new List<uint>();
        var toDemote   = new List<uint>();
        var toUnload   = new List<uint>();

        // Pass 1: walk new far window — emit ToLoadFar / ToLoadNear / ToPromote.
        var newCenterIds = new HashSet<uint>();
        for (int dx = -FarRadius; dx <= FarRadius; dx++)
        {
            for (int dy = -FarRadius; dy <= FarRadius; dy++)
            {
                int nx = newCx + dx;
                int ny = newCy + dy;
                if (nx < 0 || nx > 0xFF || ny < 0 || ny > 0xFF) continue;
                int absDx = Math.Abs(dx);
                int absDy = Math.Abs(dy);
                bool inNear = absDx <= NearRadius && absDy <= NearRadius;
                var id = EncodeLandblockId(nx, ny);
                newCenterIds.Add(id);

                if (!_tierResidence.TryGetValue(id, out var current))
                {
                    // Not resident at all — fresh load.
                    if (inNear) toLoadNear.Add(id);
                    else        toLoadFar.Add(id);
                    _tierResidence[id] = inNear ? TierResidence.Near : TierResidence.Far;
                }
                else if (current == TierResidence.Far && inNear)
                {
                    // Was Far, now inside Near ring — promote.
                    toPromote.Add(id);
                    _tierResidence[id] = TierResidence.Near;
                }
                // Near→Near and Far→Far are no-ops.
            }
        }

        foreach (var kvp in _tierResidence.ToArray())
        {
            var id      = kvp.Key;
            var current = kvp.Value;
            int lbX     = (int)((id >> 24) & 0xFFu);
            int lbY     = (int)((id >> 16) & 0xFFu);
            int absDx   = Math.Abs(lbX - newCx);
            int absDy   = Math.Abs(lbY - newCy);
            int distance = Math.Max(absDx, absDy);

            if (newCenterIds.Contains(id))
            {
                // Still in the far window — only Near→Far demote possible here.
                if (current == TierResidence.Near && (absDx > NearRadius || absDy > NearRadius))
                {
                    if (distance > nearUnloadThreshold)
                    {
                        toDemote.Add(id);
                        _tierResidence[id] = TierResidence.Far;
                    }
                }
                continue;
            }

            // Outside the new window — demote / unload by threshold.
            if (current == TierResidence.Near)
            {
                if (distance > nearUnloadThreshold)
                {
                    toDemote.Add(id);
                    _tierResidence[id] = TierResidence.Far;
                    if (distance > farUnloadThreshold)
                    {
                        toUnload.Add(id);
                        _tierResidence.Remove(id);
                    }
                }
            }
            else if (current == TierResidence.Far)
            {
                if (distance > farUnloadThreshold)
                {
                    toUnload.Add(id);
                    _tierResidence.Remove(id);
                }
            }
        }

        CenterX = newCx;
        CenterY = newCy;

        return new TwoTierDiff(toLoadFar, toLoadNear, toPromote, toDemote, toUnload);
    }

    public RegionDiff RecenterToSingleTier(int newCx, int newCy)
    {
        // Snapshot the old resident set so we can diff against it.
        var oldResident = new HashSet<uint>(_resident);

        // Recompute _visible strictly as the new window.
        Recenter(newCx, newCy);

        // Loads = entries in the new window not yet in the resident set.
        var toLoad = new List<uint>();
        foreach (var id in _visible)
            if (!oldResident.Contains(id))
                toLoad.Add(id);

        // Unloads = resident entries outside the hysteresis threshold
        //           (|dx| > Radius+2 OR |dy| > Radius+2).
        int unloadThreshold = Radius + 2;
        var toUnload = new List<uint>();
        foreach (var id in oldResident)
        {
            if (_visible.Contains(id)) continue;  // still in window, keep
            int lbX = (int)((id >> 24) & 0xFFu);
            int lbY = (int)((id >> 16) & 0xFFu);
            int dx = Math.Abs(lbX - newCx);
            int dy = Math.Abs(lbY - newCy);
            if (dx > unloadThreshold || dy > unloadThreshold)
                toUnload.Add(id);
        }

        // Update resident: (oldResident ∪ newVisible) ∖ toUnload.
        _resident.UnionWith(_visible);
        foreach (var id in toUnload)
            _resident.Remove(id);

        return new RegionDiff(toLoad, toUnload);
    }
}

public readonly record struct RegionDiff(
    IReadOnlyList<uint> ToLoad,
    IReadOnlyList<uint> ToUnload);

internal enum TierResidence { Far, Near }
