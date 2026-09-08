using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using DatReaderWriter.Types;

namespace AcDream.Core.Lighting;

public sealed class LightingHookSink : IAnimationHookSink
{
    private readonly LightManager _lights;
    private readonly IEntityEffectPoseSource _poses;
    private readonly IEntityEffectCellSource? _cells;

    // Index owner → the set of LightSource instances they registered.
    // Maintained lazily — populated on first RegisterLight for that owner.
    private readonly Dictionary<uint, List<LightSource>> _byOwner = new();
    private readonly Dictionary<uint, List<LightSource>> _trackedByOwner = new();
    private readonly Dictionary<uint, bool> _enabledByOwner = new();

    public event Action<uint, bool>? OwnerLightingChanged;

    public int OwnedLightOwnerCount => _byOwner.Count;
    public int PoseTrackedOwnerCount => _trackedByOwner.Count;
    public int RetainedOwnerStateCount => _enabledByOwner.Count;

    public LightingHookSink(LightManager lights, IEntityEffectPoseSource poses)
    {
        _lights = lights ?? throw new System.ArgumentNullException(nameof(lights));
        _poses = poses ?? throw new System.ArgumentNullException(nameof(poses));
        _cells = poses as IEntityEffectCellSource;
    }

    public void RegisterOwnedLight(LightSource light)
    {
        System.ArgumentNullException.ThrowIfNull(light);
        if (_enabledByOwner.TryGetValue(light.OwnerId, out bool enabled))
            light.IsLit = enabled;
        _lights.Register(light);
        if (!_byOwner.TryGetValue(light.OwnerId, out var list))
        {
            list = new List<LightSource>();
            _byOwner[light.OwnerId] = list;
        }
        list.Add(light);
        if (light.TracksOwnerPose)
        {
            if (!_trackedByOwner.TryGetValue(light.OwnerId, out List<LightSource>? tracked))
            {
                tracked = new List<LightSource>();
                _trackedByOwner[light.OwnerId] = tracked;
            }
            tracked.Add(light);
        }
    }

    /// <summary>Drop every light tagged to this owner (despawn / unload).</summary>
    public void UnregisterOwner(uint ownerId, bool forgetState = true)
    {
        if (_byOwner.TryGetValue(ownerId, out var list))
        {
            foreach (var l in list) _lights.Unregister(l);
            _byOwner.Remove(ownerId);
        }
        _trackedByOwner.Remove(ownerId);
        if (forgetState)
            _enabledByOwner.Remove(ownerId);
    }

    public void InitializeOwnerLighting(uint ownerId, bool enabled) =>
        _enabledByOwner.TryAdd(ownerId, enabled);

    public bool IsOwnerLightingEnabled(uint ownerId) =>
        !_enabledByOwner.TryGetValue(ownerId, out bool enabled) || enabled;

    public void SetOwnerLighting(uint ownerId, bool enabled)
    {
        if (_enabledByOwner.TryGetValue(ownerId, out bool current)
            && current == enabled)
        {
            return;
        }
        _enabledByOwner[ownerId] = enabled;
        if (_byOwner.TryGetValue(ownerId, out var list))
        {
            foreach (LightSource light in list)
                light.IsLit = enabled;
        }
        OwnerLightingChanged?.Invoke(ownerId, enabled);
    }

    public IReadOnlyList<LightSource>? GetOwnedLights(uint ownerId)
    {
        return _byOwner.TryGetValue(ownerId, out var list) ? list : null;
    }

    public void RefreshAttachedLights()
    {
        foreach ((uint ownerId, List<LightSource> lights) in _trackedByOwner)
        {
            if (!_poses.TryGetRootPose(ownerId, out Matrix4x4 rootWorld))
                continue;

            uint cellId = 0;
            _cells?.TryGetCellId(ownerId, out cellId);
            for (int i = 0; i < lights.Count; i++)
            {
                LightSource light = lights[i];
                Matrix4x4 lightWorld = light.LocalPose * rootWorld;
                light.RankingOrigin = rootWorld.Translation;
                light.WorldPosition = lightWorld.Translation;
                Vector3 forward = Vector3.TransformNormal(Vector3.UnitY, lightWorld);
                if (forward.LengthSquared() > 1e-8f)
                    light.WorldForward = Vector3.Normalize(forward);
                light.CellId = cellId;
            }
        }
    }

    public void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook)
    {
        if (hook is not SetLightHook slh) return;
        SetOwnerLighting(entityId, slh.LightsOn);
    }
}
