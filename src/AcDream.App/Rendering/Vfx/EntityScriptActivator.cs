using System.Numerics;
using AcDream.Core.Vfx;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Vfx;

/// <summary>
/// Setup script/profile data resolved once when a logical effect owner is
/// registered. Part transforms are indexed and root-local.
/// </summary>
public sealed record ScriptActivationInfo(
    uint ScriptId,
    IReadOnlyList<Matrix4x4> PartTransforms,
    EntityEffectProfile? EffectProfile = null,
    IReadOnlyList<bool>? PartAvailability = null,
    DatReaderWriter.DBObjs.Setup? Setup = null,
    uint DefaultAnimationId = 0,
    bool UsesStaticAnimationWorkset = false);

public sealed class EntityScriptActivator
{
    private sealed class StaticOwnerState
    {
        public required WorldEntity Entity;
        public required ScriptActivationInfo Info;
        public bool PosePublished;
        public bool EffectOwnerRegistered;
        public bool AnimationOwnerRegistered;
        public bool ScriptStarted;
        public bool ReleaseStarted;
        public bool ScriptsStopped;
        public bool EmittersStopped;
        public bool PoseRemoved;
        public WorldEntity? PendingReplacement;
        public ScriptActivationInfo? PendingReplacementInfo;
        public bool ReplacementPosePublished;
        public bool ReplacementEffectOwnerUpdated;
        public bool ReplacementAnimationOwnerUpdated;
        public bool ReplacementAnchorUpdated;
    }

    private readonly PhysicsScriptRunner _scriptRunner;
    private readonly ParticleHookSink _particleSink;
    private readonly EntityEffectPoseRegistry _poses;
    private readonly Func<WorldEntity, ScriptActivationInfo?> _resolver;
    private readonly Action<uint, WorldEntity, EntityEffectProfile>? _registerDatStaticEffectOwner;
    private readonly Action<uint>? _unregisterDatStaticEffectOwner;
    private readonly Func<WorldEntity, ScriptActivationInfo, bool>? _registerDatStaticAnimationOwner;
    private readonly Action<uint>? _unregisterDatStaticAnimationOwner;
    private readonly Action<WorldEntity, ScriptActivationInfo>? _rebindDatStaticAnimationOwner;
    private readonly Dictionary<uint, StaticOwnerState> _staticOwners = new();

    public EntityScriptActivator(
        PhysicsScriptRunner scriptRunner,
        ParticleHookSink particleSink,
        EntityEffectPoseRegistry poses,
        Func<WorldEntity, ScriptActivationInfo?> resolver,
        Action<uint, WorldEntity, EntityEffectProfile>? registerDatStaticEffectOwner = null,
        Action<uint>? unregisterDatStaticEffectOwner = null,
        Func<WorldEntity, ScriptActivationInfo, bool>? registerDatStaticAnimationOwner = null,
        Action<uint>? unregisterDatStaticAnimationOwner = null,
        Action<WorldEntity, ScriptActivationInfo>? rebindDatStaticAnimationOwner = null)
    {
        _scriptRunner = scriptRunner ?? throw new ArgumentNullException(nameof(scriptRunner));
        _particleSink = particleSink ?? throw new ArgumentNullException(nameof(particleSink));
        _poses = poses ?? throw new ArgumentNullException(nameof(poses));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _registerDatStaticEffectOwner = registerDatStaticEffectOwner;
        _unregisterDatStaticEffectOwner = unregisterDatStaticEffectOwner;
        _registerDatStaticAnimationOwner = registerDatStaticAnimationOwner;
        _unregisterDatStaticAnimationOwner = unregisterDatStaticAnimationOwner;
        _rebindDatStaticAnimationOwner = rebindDatStaticAnimationOwner;
    }

    public void OnRebindStatic(WorldEntity previous, WorldEntity replacement)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(replacement);
        if (previous.ServerGuid != 0
            || replacement.ServerGuid != 0
            || previous.Id == 0
            || previous.Id != replacement.Id)
        {
            throw new ArgumentException(
                "DAT-static rebind requires the same nonzero local ID.",
                nameof(replacement));
        }

        uint key = previous.Id;
        if (!_staticOwners.TryGetValue(key, out StaticOwnerState? state))
            return;
        if (ReferenceEquals(state.Entity, replacement))
            return;
        if (!ReferenceEquals(state.Entity, previous))
        {
            throw new InvalidOperationException(
                $"DAT-static owner 0x{key:X8} does not match the retained incarnation.");
        }
        if (state.ReleaseStarted)
        {
            throw new InvalidOperationException(
                $"DAT-static owner 0x{key:X8} cannot rebind while teardown is pending.");
        }

        if (state.PendingReplacement is null)
        {
            ScriptActivationInfo replacementInfo = _resolver(replacement)
                ?? throw new InvalidOperationException(
                    $"DAT-static owner 0x{key:X8} lost its Setup activation data during rebind.");
            ValidateLogicalRebind(state, replacement, replacementInfo);
            state.PendingReplacement = replacement;
            state.PendingReplacementInfo = replacementInfo;
        }
        else if (!ReferenceEquals(state.PendingReplacement, replacement))
        {
            throw new InvalidOperationException(
                $"DAT-static owner 0x{key:X8} already has another rebind pending.");
        }

        ScriptActivationInfo info = state.PendingReplacementInfo!;
        if (state.PosePublished && !state.ReplacementPosePublished)
        {
            _poses.Publish(replacement, info.PartTransforms, info.PartAvailability);
            state.ReplacementPosePublished = true;
        }
        if (state.EffectOwnerRegistered && !state.ReplacementEffectOwnerUpdated)
        {
            _registerDatStaticEffectOwner!(key, replacement, info.EffectProfile!);
            state.ReplacementEffectOwnerUpdated = true;
        }
        if (state.AnimationOwnerRegistered && !state.ReplacementAnimationOwnerUpdated)
        {
            (_rebindDatStaticAnimationOwner
                ?? throw new InvalidOperationException(
                    "DAT-static animation ownership has no retained-owner rebind callback."))(
                replacement,
                info);
            state.ReplacementAnimationOwnerUpdated = true;
        }
        if (!state.ReplacementAnchorUpdated)
        {
            _scriptRunner.SetOwnerAnchor(key, replacement.Position);
            state.ReplacementAnchorUpdated = true;
        }

        state.Entity = replacement;
        state.Info = info;
        state.PendingReplacement = null;
        state.PendingReplacementInfo = null;
        state.ReplacementPosePublished = false;
        state.ReplacementEffectOwnerUpdated = false;
        state.ReplacementAnimationOwnerUpdated = false;
        state.ReplacementAnchorUpdated = false;
    }

    private static void ValidateLogicalRebind(
        StaticOwnerState state,
        WorldEntity replacement,
        ScriptActivationInfo replacementInfo)
    {
        ScriptActivationInfo retained = state.Info;
        bool retainedAnimation = retained.UsesStaticAnimationWorkset
            && retained.DefaultAnimationId != 0;
        bool replacementAnimation = replacementInfo.UsesStaticAnimationWorkset
            && replacementInfo.DefaultAnimationId != 0;
        if (state.Entity.SourceGfxObjOrSetupId != replacement.SourceGfxObjOrSetupId
            || retained.ScriptId != replacementInfo.ScriptId
            || retainedAnimation != replacementAnimation
            || (retainedAnimation
                && retained.DefaultAnimationId != replacementInfo.DefaultAnimationId)
            || (retained.EffectProfile is null) != (replacementInfo.EffectProfile is null))
        {
            throw new InvalidOperationException(
                $"DAT-static owner 0x{state.Entity.Id:X8} changed Setup lifetime during a retained rebind.");
        }
    }

    public void OnCreate(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        uint key = entity.Id;
        if (key == 0)
            return;

        if (entity.ServerGuid == 0
            && _staticOwners.TryGetValue(key, out StaticOwnerState? retained))
        {
            if (!ReferenceEquals(retained.Entity, entity))
            {
                throw new InvalidOperationException(
                    $"Dat-static WorldEntity id 0x{key:X8} is already active; static allocators must be globally unique.");
            }
            if (retained.ReleaseStarted)
            {
                throw new InvalidOperationException(
                    $"Dat-static WorldEntity id 0x{key:X8} cannot reactivate while teardown is pending.");
            }

            ActivateStaticOwner(retained);
            return;
        }

        ScriptActivationInfo? info = _resolver(entity);
        if (info is null)
            return;

        if (entity.ServerGuid == 0)
        {
            var state = new StaticOwnerState
            {
                Entity = entity,
                Info = info,
            };
            _staticOwners.Add(key, state);
            ActivateStaticOwner(state);
            return;
        }

        // Live registration is an atomic logical-lifetime edge owned by
        // LiveEntityRuntime and is never retried at this activator in isolation.
        _poses.Publish(entity, info.PartTransforms, info.PartAvailability);
        if (info.UsesStaticAnimationWorkset
            && info.DefaultAnimationId != 0)
        {
            _registerDatStaticAnimationOwner?.Invoke(entity, info);
        }
        if (info.ScriptId != 0)
            _scriptRunner.Play(info.ScriptId, key, entity.Position);
    }

    private void ActivateStaticOwner(StaticOwnerState state)
    {
        WorldEntity entity = state.Entity;
        ScriptActivationInfo info = state.Info;
        uint key = entity.Id;

        if (!state.PosePublished)
        {
            _poses.Publish(entity, info.PartTransforms, info.PartAvailability);
            state.PosePublished = true;
        }

        if (!state.EffectOwnerRegistered
            && info.EffectProfile is { } profile
            && _registerDatStaticEffectOwner is not null)
        {
            _registerDatStaticEffectOwner(key, entity, profile);
            state.EffectOwnerRegistered = true;
        }

        if (!state.AnimationOwnerRegistered
            && info.UsesStaticAnimationWorkset
            && info.DefaultAnimationId != 0
            && _registerDatStaticAnimationOwner is not null)
        {
            state.AnimationOwnerRegistered =
                _registerDatStaticAnimationOwner(entity, info);
        }

        if (!state.ScriptStarted && info.ScriptId != 0)
        {
            _scriptRunner.Play(info.ScriptId, key, entity.Position);
            state.ScriptStarted = true;
        }
    }

    public void OnRemove(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        uint key = entity.Id;
        if (key == 0)
            return;

        if (entity.ServerGuid == 0)
        {
            if (!_staticOwners.TryGetValue(key, out StaticOwnerState? state)
                || !ReferenceEquals(state.Entity, entity))
            {
                return;
            }

            state.ReleaseStarted = true;
            if (!state.ScriptsStopped)
            {
                _scriptRunner.StopAllForEntity(key);
                state.ScriptsStopped = true;
            }
            if (!state.EmittersStopped)
            {
                _particleSink.StopAllForEntity(key, fadeOut: false);
                state.EmittersStopped = true;
            }
            if (!state.PoseRemoved)
            {
                _poses.Remove(key);
                state.PoseRemoved = true;
            }
            if (state.AnimationOwnerRegistered)
            {
                _unregisterDatStaticAnimationOwner?.Invoke(key);
                state.AnimationOwnerRegistered = false;
            }
            if (state.EffectOwnerRegistered)
            {
                _unregisterDatStaticEffectOwner?.Invoke(key);
                state.EffectOwnerRegistered = false;
            }

            _staticOwners.Remove(key);
            return;
        }

        _unregisterDatStaticAnimationOwner?.Invoke(key);
        _scriptRunner.StopAllForEntity(key);
        _particleSink.StopAllForEntity(key, fadeOut: false);
        _poses.Remove(key);
    }
}
