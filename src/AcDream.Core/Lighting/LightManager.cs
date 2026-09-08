using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.Lighting;

public sealed class LightManager
{
    public const int MaxActiveLights = 8;   // D3D parity

    private readonly List<LightSource> _all = new();
    private readonly LightSource?[] _active = new LightSource?[MaxActiveLights];
    private int _activeCount;
    private LightSource? _viewerLight;

    public CellAmbientState CurrentAmbient { get; set; }

    public LightSource? Sun { get; set; }

    public ReadOnlySpan<LightSource?> Active => _active.AsSpan(0, _activeCount);

    public int ActiveCount => _activeCount;
    public int RegisteredCount => _all.Count;

    /// <summary>Add a light. Idempotent — adding the same instance twice is a no-op.</summary>
    public void Register(LightSource light)
    {
        ArgumentNullException.ThrowIfNull(light);
        if (light.Kind != LightKind.Directional && !light.HasRankingOrigin)
            throw new ArgumentException(
                "A nondirectional light requires an explicit authored ranking origin.",
                nameof(light));
        foreach (var existing in _all)
            if (ReferenceEquals(existing, light)) return;
        _all.Add(light);
    }

    /// <summary>Remove by reference.</summary>
    public void Unregister(LightSource light)
    {
        _all.Remove(light);
    }

    /// <summary>Remove every light attached to a specific entity.</summary>
    public void UnregisterByOwner(uint ownerId)
    {
        _all.RemoveAll(l => l.OwnerId == ownerId);
    }

    public void Clear()
    {
        _all.Clear();
        Array.Clear(_active);
        _activeCount = 0;
        _viewerLight = null;
        _pointSnapshot.Clear();
        _dynamicPointSnapshot.Clear();
        _staticPointSnapshot.Clear();
    }

    public void Tick(Vector3 viewerWorldPos)
    {
        Array.Clear(_active);
        _activeCount = 0;

        // Slot 0 = sun when present (directional; never ranked by distance).
        int baseSlot = 0;
        if (Sun is not null)
        {
            _active[0] = Sun;
            baseSlot = 1;
        }

        int maxPoint = MaxActiveLights - baseSlot;
        int filled = 0;
        if (maxPoint > 0)
        {
            foreach (var light in _all)
            {
                if (!light.IsLit || light.Kind == LightKind.Directional) continue;

                Vector3 delta = light.WorldPosition - viewerWorldPos;
                light.DistSq = delta.LengthSquared();

                if (filled < maxPoint)
                {
                    int j = baseSlot + filled;
                    while (j > baseSlot && _active[j - 1]!.DistSq > light.DistSq)
                    {
                        _active[j] = _active[j - 1];
                        j--;
                    }
                    _active[j] = light;
                    filled++;
                }
                else if (light.DistSq < _active[baseSlot + maxPoint - 1]!.DistSq)
                {
                    int j = baseSlot + maxPoint - 1;
                    while (j > baseSlot && _active[j - 1]!.DistSq > light.DistSq)
                    {
                        _active[j] = _active[j - 1];
                        j--;
                    }
                    _active[j] = light;
                }
            }
        }

        _activeCount = baseSlot + filled;
    }


    public const int MaxLightsPerObject = 8;

    public const int MaxDynamicPointLights = 7;

    public const int MaxStaticPointLights = 40;

    public const int MaxGlobalLights = MaxDynamicPointLights + MaxStaticPointLights;

    public const int MaxLightsPerEnvCell = MaxGlobalLights;

    private readonly List<LightSource> _pointSnapshot = new();

    public IReadOnlyList<LightSource> PointSnapshot => _pointSnapshot;
    private readonly List<LightSource> _dynamicPointSnapshot =
        new(MaxDynamicPointLights);
    private readonly List<LightSource> _staticPointSnapshot =
        new(MaxStaticPointLights);

    public void BuildPointLightSnapshot(Vector3 playerWorldPos)
    {
        _pointSnapshot.Clear();
        _dynamicPointSnapshot.Clear();
        _staticPointSnapshot.Clear();

        if (_viewerLight is { IsLit: true } viewer)
            InsertRetained(viewer, playerWorldPos, _dynamicPointSnapshot, MaxDynamicPointLights);

        foreach (var light in _all)
        {
            if (ReferenceEquals(light, _viewerLight)
                || !light.IsLit
                || light.Kind == LightKind.Directional)
                continue;

            InsertRetained(
                light,
                playerWorldPos,
                light.IsDynamic ? _dynamicPointSnapshot : _staticPointSnapshot,
                light.IsDynamic ? MaxDynamicPointLights : MaxStaticPointLights);
        }

        _pointSnapshot.AddRange(_dynamicPointSnapshot);
        _pointSnapshot.AddRange(_staticPointSnapshot);
    }

    private static void InsertRetained(
        LightSource light,
        Vector3 playerWorldPos,
        List<LightSource> product,
        int capacity)
    {
        float rank = light.Kind == LightKind.Point
            ? Vector3.DistanceSquared(light.RankingOrigin, playerWorldPos)
            : 0f;
        light.DistSq = rank;

        int index = 0;
        while (index < product.Count && !(rank < product[index].DistSq))
            index++;
        if (index >= capacity)
            return;

        product.Insert(index, light);
        if (product.Count > capacity)
            product.RemoveAt(capacity);
    }

    public const float ViewerLightIntensity = 2.25f;
    public const float ViewerLightFalloff   = 10f;
    private const uint ViewerLightOwnerId   = 0xFFFFFFFFu;  // sentinel; never an entity id

    public void UpdateViewerLight(Vector3 playerWorldPos)
    {
        if (_viewerLight is null)
        {
            _viewerLight = new LightSource
            {
                Kind        = LightKind.Point,
                LocalPose   = Matrix4x4.CreateTranslation(0f, 0f, 2f),
                ColorLinear = Vector3.One,                  // white (1,1,1)
                Intensity   = ViewerLightIntensity,
                Range       = ViewerLightFalloff * 1.5f,    // dynamic rangeAdjust 1.5
                OwnerId     = ViewerLightOwnerId,
                IsLit       = true,
                IsDynamic   = true,
            };
            _all.Add(_viewerLight);
        }
        _viewerLight.RankingOrigin = playerWorldPos;
        _viewerLight.WorldPosition = playerWorldPos + new Vector3(0f, 0f, 2f);
    }

    public static int SelectForObject(
        IReadOnlyList<LightSource> snapshot,
        Vector3 center,
        float radius,
        Span<int> outIndices)
    {
        int cap = Math.Min(outIndices.Length, MaxLightsPerObject);
        if (cap <= 0) return 0;

        Span<float> keptDistSq = stackalloc float[MaxLightsPerObject];
        int count = 0;

        for (int li = 0; li < snapshot.Count; li++)
        {
            var light = snapshot[li];
            float reach = light.Range + radius;
            float dsq = (light.WorldPosition - center).LengthSquared();
            if (dsq >= reach * reach) continue;   // light's sphere doesn't reach the object

            if (count < cap)
            {
                int j = count;
                while (j > 0 && keptDistSq[j - 1] > dsq)
                {
                    keptDistSq[j] = keptDistSq[j - 1];
                    outIndices[j] = outIndices[j - 1];
                    j--;
                }
                keptDistSq[j] = dsq;
                outIndices[j] = li;
                count++;
            }
            else if (dsq < keptDistSq[cap - 1])
            {
                int j = cap - 1;
                while (j > 0 && keptDistSq[j - 1] > dsq)
                {
                    keptDistSq[j] = keptDistSq[j - 1];
                    outIndices[j] = outIndices[j - 1];
                    j--;
                }
                keptDistSq[j] = dsq;
                outIndices[j] = li;
            }
        }
        return count;
    }

    public static int SelectForCell(
        IReadOnlyList<LightSource> snapshot,
        Span<int> outIndices)
    {
        int count = Math.Min(
            Math.Min(snapshot.Count, outIndices.Length),
            MaxLightsPerEnvCell);
        for (int index = 0; index < count; index++)
            outIndices[index] = index;
        return count;
    }
}
