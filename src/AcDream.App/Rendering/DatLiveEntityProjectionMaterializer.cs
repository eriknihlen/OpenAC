using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Core.Meshing;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Plugins;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Rendering;

internal sealed class DatLiveEntityProjectionMaterializer
    : ILiveEntityProjectionMaterializer
{
    private readonly RuntimeOptions _options;
    private readonly IDatReaderWriter _dats;
    private readonly LiveEntityRuntime _runtime;
    private readonly LiveCollisionAssetPublisher _collisionAssets;
    private readonly IAnimationLoader _animationLoader;
    private readonly EntitySpawnAdapter _spawnAdapter;
    private readonly TextureCache _textures;
    private readonly EntityClassificationCache _classification;
    private readonly EntityEffectPoseRegistry _effectPoses;
    private readonly EquippedChildRenderController _equippedChildren;
    private readonly WorldGameState _worldState;
    private readonly WorldEvents _worldEvents;
    private readonly ShadowObjectRegistry _shadows;
    private readonly LiveEntityCollisionBuilder _collisionBuilder;
    private readonly ProjectileController _projectiles;
    private readonly LiveEntityAnimationPresenter _animationPresenter;
    private readonly RetailStaticAnimatingObjectScheduler _staticAnimations;
    private readonly LiveWorldOriginState _origin;
    private readonly IPhysicsScriptTimeSource _gameTime;
    private readonly RuntimeWorldTransitState _transit;

    public DatLiveEntityProjectionMaterializer(
        RuntimeOptions options,
        IDatReaderWriter dats,
        LiveEntityRuntime runtime,
        LiveCollisionAssetPublisher collisionAssets,
        IAnimationLoader animationLoader,
        EntitySpawnAdapter spawnAdapter,
        TextureCache textures,
        EntityClassificationCache classification,
        EntityEffectPoseRegistry effectPoses,
        EquippedChildRenderController equippedChildren,
        WorldGameState worldState,
        WorldEvents worldEvents,
        ShadowObjectRegistry shadows,
        LiveEntityCollisionBuilder collisionBuilder,
        ProjectileController projectiles,
        LiveEntityAnimationPresenter animationPresenter,
        RetailStaticAnimatingObjectScheduler staticAnimations,
        LiveWorldOriginState origin,
        IPhysicsScriptTimeSource gameTime,
        RuntimeWorldTransitState transit)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _collisionAssets = collisionAssets ??
            throw new ArgumentNullException(nameof(collisionAssets));
        _animationLoader = animationLoader ?? throw new ArgumentNullException(nameof(animationLoader));
        _spawnAdapter = spawnAdapter ?? throw new ArgumentNullException(nameof(spawnAdapter));
        _textures = textures ?? throw new ArgumentNullException(nameof(textures));
        _classification = classification ?? throw new ArgumentNullException(nameof(classification));
        _effectPoses = effectPoses ?? throw new ArgumentNullException(nameof(effectPoses));
        _equippedChildren = equippedChildren ?? throw new ArgumentNullException(nameof(equippedChildren));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _worldEvents = worldEvents ?? throw new ArgumentNullException(nameof(worldEvents));
        _shadows = shadows ?? throw new ArgumentNullException(nameof(shadows));
        _collisionBuilder = collisionBuilder ?? throw new ArgumentNullException(nameof(collisionBuilder));
        _projectiles = projectiles ?? throw new ArgumentNullException(nameof(projectiles));
        _animationPresenter = animationPresenter ?? throw new ArgumentNullException(nameof(animationPresenter));
        _staticAnimations = staticAnimations ?? throw new ArgumentNullException(nameof(staticAnimations));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _gameTime = gameTime ?? throw new ArgumentNullException(nameof(gameTime));
        _transit = transit ?? throw new ArgumentNullException(nameof(transit));
    }

    public void ResetSessionState()
    {
    }

    public bool TryMaterialize(
        RuntimeEntityRecord expectedCanonical,
        WorldSession.EntitySpawn canonicalSpawn,
        LiveProjectionPurpose purpose,
        ulong expectedCreateIntegrationVersion,
        LiveEntityAppearanceUpdateState? appearanceUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(expectedCanonical);
        if (purpose is LiveProjectionPurpose.AppearanceMutation
            != (appearanceUpdate is not null))
        {
            throw new ArgumentException(
                "Appearance mutation requires the exact captured visual owner.",
                nameof(appearanceUpdate));
        }
        if (!_runtime.IsCurrentCreateIntegration(
                expectedCanonical,
                expectedCreateIntegrationVersion)
            || canonicalSpawn.Guid != expectedCanonical.ServerGuid
            || canonicalSpawn.InstanceSequence != expectedCanonical.Incarnation)
        {
            return false;
        }
        _runtime.TryGetProjection(
            expectedCanonical,
            out LiveEntityRecord? expectedRecord);
        if (appearanceUpdate is not null && expectedRecord is null)
            return false;

        if (!_origin.IsKnown)
            return false;
        if (canonicalSpawn.Position is null || canonicalSpawn.SetupTableId is null)
        {
            return false;
        }

        CreateObject.ServerPosition position = canonicalSpawn.Position.Value;
        int lbX = (int)((position.LandblockId >> 24) & 0xFFu);
        int lbY = (int)((position.LandblockId >> 16) & 0xFFu);
        if (!_origin.TryEnsureAgreesWithRuntimeFrame(
                _runtime.Physics.WorldFrameCenterLandblockId,
                position.LandblockId,
                transitInFlight: _transit.IsTeleportActive))
        {
            return false;
        }
        if (AcDream.Core.Physics.PhysicsDiagnostics.ProbeWorldFrameEnabled)
            ProbeWorldFrameAgreement(position.LandblockId);
        var worldOrigin = new Vector3(
            (lbX - _origin.CenterX) * 192f,
            (lbY - _origin.CenterY) * 192f,
            0f);
        Vector3 worldPosition = new(
            position.PositionX + worldOrigin.X,
            position.PositionY + worldOrigin.Y,
            position.PositionZ);
        var rotation = new Quaternion(
            position.RotationX,
            position.RotationY,
            position.RotationZ,
            position.RotationW);

        Setup? setup = _dats.Get<Setup>(canonicalSpawn.SetupTableId.Value);
        if (setup is not null)
            _collisionAssets.CacheSetup(canonicalSpawn.SetupTableId.Value, setup);
        if (setup is null)
        {
            return false;
        }

        _runtime.SetHasPartArray(expectedCanonical, true);
        ushort? stanceOverride = canonicalSpawn.MotionState?.Stance;
        ushort? commandOverride = canonicalSpawn.MotionState?.ForwardCommand;
        var idleCycle = MotionResolver.GetIdleCycle(
            setup,
            _dats,
            _animationLoader,
            motionTableIdOverride: canonicalSpawn.MotionTableId,
            stanceOverride: stanceOverride,
            commandOverride: commandOverride);
        AnimationFrame? idleFrame = null;
        if (idleCycle is not null)
        {
            int startIndex = idleCycle.LowFrame;
            if (startIndex < 0 || startIndex >= idleCycle.Animation.PartFrames.Count)
                startIndex = 0;
            idleFrame = idleCycle.Animation.PartFrames[startIndex];
        }

        List<MeshRef> flattened = [.. SetupMesh.Flatten(setup, idleFrame)];
        IReadOnlyList<CreateObject.AnimPartChange> animPartChanges =
            canonicalSpawn.AnimPartChanges ?? Array.Empty<CreateObject.AnimPartChange>();

        foreach (CreateObject.AnimPartChange change in animPartChanges)
        {
            if (change.PartIndex < flattened.Count)
            {
                flattened[change.PartIndex] = new MeshRef(
                    change.NewModelId,
                    flattened[change.PartIndex].PartTransform);
            }
        }

        uint[] collisionPartGfxObjIds =
            LiveEntityCollisionBuilder.ResolveEffectivePartIdentities(
                flattened.Select(static part => part.GfxObjId).ToArray(),
                baseId => ResolveCollisionPart(baseId));

        if (_options.RetailCloseDegrades && HasHumanoidNullPartLayout(setup))
            ApplyRetailCloseDegrades(flattened);

        IReadOnlyList<CreateObject.TextureChange> textureChanges =
            canonicalSpawn.TextureChanges ?? Array.Empty<CreateObject.TextureChange>();
        Dictionary<int, Dictionary<uint, uint>>? surfaceOverrides =
            ResolveSurfaceOverrides(
                flattened,
                textureChanges);

        float scale = canonicalSpawn.ObjScale ?? 1f;
        Matrix4x4 scaleMatrix = Matrix4x4.CreateScale(scale);
        IReadOnlyList<Matrix4x4> rigidPartTransforms =
            SetupPartTransforms.Compute(setup, idleFrame, scale);
        var meshRefs = new List<MeshRef>();
        var indexedPartTransforms = new Matrix4x4[flattened.Count];
        var indexedPartAvailable = new bool[flattened.Count];
        var animatedPartTemplate = new LiveAnimationPartTemplate[flattened.Count];
        var bounds = new LocalBoundsAccumulator();

        for (int partIndex = 0; partIndex < flattened.Count; partIndex++)
        {
            MeshRef part = flattened[partIndex];
            IReadOnlyDictionary<uint, uint>? overrides = null;
            if (surfaceOverrides?.TryGetValue(partIndex, out var found) == true)
                overrides = found;

            Matrix4x4 transform = scale == 1f
                ? part.PartTransform
                : part.PartTransform * scaleMatrix;
            indexedPartTransforms[partIndex] = partIndex < rigidPartTransforms.Count
                ? rigidPartTransforms[partIndex]
                : Matrix4x4.Identity;
            GfxObj? gfx = _dats.Get<GfxObj>(part.GfxObjId);
            bool drawable = gfx is not null;
            indexedPartAvailable[partIndex] = drawable;
            animatedPartTemplate[partIndex] = new LiveAnimationPartTemplate(
                part.GfxObjId,
                overrides,
                drawable);
            if (gfx is null)
            {
                continue;
            }

            _collisionAssets.CacheGfxObj(part.GfxObjId, gfx);

            if (GfxObjBounds.Get(gfx) is { } partBounds)
                bounds.Add(transform, partBounds);
            meshRefs.Add(new MeshRef(part.GfxObjId, transform)
            {
                SurfaceOverrides = overrides,
            });
        }

        if (meshRefs.Count == 0)
        {
            return false;
        }

        PaletteOverride? paletteOverride = CreatePaletteOverride(canonicalSpawn);
        PartOverride[] partOverrides = CreatePartOverrides(animPartChanges);

        bool supersessionRecovery =
            purpose is LiveProjectionPurpose.CreateSupersessionRecovery;
        if (supersessionRecovery
            && expectedRecord?.WorldEntity is not null)
        {
            return LiveEntityCreateSupersessionRecovery.TryApply(
                _runtime,
                expectedRecord,
                expectedCreateIntegrationVersion,
                captureAppearance: () => appearanceUpdate
                    ?? LiveEntityAppearanceBinding.Capture(
                        _runtime,
                        expectedRecord.ServerGuid),
                publishAppearance: visualUpdate => ApplyAppearance(
                    expectedRecord,
                    canonicalSpawn,
                    visualUpdate,
                    setup,
                    scale,
                    worldOrigin,
                    collisionPartGfxObjIds,
                    meshRefs,
                    paletteOverride,
                    partOverrides,
                    indexedPartTransforms,
                    indexedPartAvailable,
                    animatedPartTemplate,
                    bounds,
                    expectedCreateIntegrationVersion),
                publishCurrentSnapshot: visualUpdate =>
                {
                    var snapshot = new AcDream.Plugin.Abstractions.WorldEntitySnapshot(
                        visualUpdate.Entity.Id,
                        visualUpdate.Entity.SourceGfxObjOrSetupId,
                        visualUpdate.Entity.Position,
                        visualUpdate.Entity.Rotation);
                    _worldState.Add(snapshot);
                    _worldEvents.UpsertCurrent(snapshot);
                },
                synchronizeAnimation: visualUpdate => RegisterAnimation(
                    expectedRecord,
                    visualUpdate.Entity,
                    setup,
                    canonicalSpawn,
                    idleCycle,
                    scale,
                    animatedPartTemplate,
                    indexedPartAvailable,
                    retainedAnimation:
                        expectedRecord.AnimationRuntime is LiveEntityAnimationState,
                    synchronizeAnimation: true));
        }

        if (appearanceUpdate is { } visualUpdate)
        {
            return ApplyAppearance(
                expectedRecord!,
                canonicalSpawn,
                visualUpdate,
                setup,
                scale,
                worldOrigin,
                collisionPartGfxObjIds,
                meshRefs,
                paletteOverride,
                partOverrides,
                indexedPartTransforms,
                indexedPartAvailable,
                animatedPartTemplate,
                bounds,
                expectedCreateIntegrationVersion);
        }

        return MaterializeProjection(
            expectedCanonical,
            expectedRecord,
            canonicalSpawn,
            setup,
            idleCycle,
            worldOrigin,
            worldPosition,
            rotation,
            scale,
            collisionPartGfxObjIds,
            meshRefs,
            paletteOverride,
            partOverrides,
            indexedPartTransforms,
            indexedPartAvailable,
            animatedPartTemplate,
            bounds,
            expectedCreateIntegrationVersion,
            synchronizeAnimation: supersessionRecovery);
    }

    private uint ResolveCollisionPart(uint baseId)
    {
        if (!GfxObjDegradeResolver.TryResolveCloseGfxObj(
                _dats,
                baseId,
                out uint slotZeroId,
                out GfxObj? slotZeroGfx))
        {
            return baseId;
        }

        if (slotZeroGfx is not null)
            _collisionAssets.CacheGfxObj(slotZeroId, slotZeroGfx);
        return slotZeroId;
    }

    private void ApplyRetailCloseDegrades(List<MeshRef> parts)
    {
        for (int partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            MeshRef part = parts[partIndex];
            if (!GfxObjDegradeResolver.TryResolveCloseGfxObj(
                    _dats,
                    part.GfxObjId,
                    out uint resolvedId,
                    out _)
                || resolvedId == part.GfxObjId)
            {
                continue;
            }

            parts[partIndex] = new MeshRef(resolvedId, part.PartTransform);
        }
    }

    private Dictionary<int, Dictionary<uint, uint>>? ResolveSurfaceOverrides(
        IReadOnlyList<MeshRef> parts,
        IReadOnlyList<CreateObject.TextureChange> textureChanges)
    {
        if (textureChanges.Count == 0)
            return null;

        var oldToNewByPart = new Dictionary<int, Dictionary<uint, uint>>();
        foreach (CreateObject.TextureChange change in textureChanges)
        {
            if (!oldToNewByPart.TryGetValue(change.PartIndex, out var oldToNew))
            {
                oldToNew = [];
                oldToNewByPart.Add(change.PartIndex, oldToNew);
            }
            oldToNew[change.OldTexture] = change.NewTexture;
        }

        var result = new Dictionary<int, Dictionary<uint, uint>>();
        for (int partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            if (!oldToNewByPart.TryGetValue(partIndex, out var oldToNew))
                continue;

            GfxObj? gfx = _dats.Get<GfxObj>(parts[partIndex].GfxObjId);
            if (gfx is null)
            {
                continue;
            }
            _collisionAssets.CacheGfxObj(parts[partIndex].GfxObjId, gfx);

            Dictionary<uint, uint>? resolved = null;
            foreach (var surfaceQid in gfx.Surfaces)
            {
                uint surfaceId = (uint)surfaceQid;
                Surface? surface = _dats.Get<Surface>(surfaceId);
                if (surface is null)
                    continue;
                uint originalTexture = (uint)surface.OrigTextureId;
                if (originalTexture == 0
                    || !oldToNew.TryGetValue(originalTexture, out uint newTexture))
                {
                    continue;
                }

                (resolved ??= [])[surfaceId] = newTexture;
            }

            if (resolved is not null)
                result[partIndex] = resolved;
        }

        return result.Count == 0 ? null : result;
    }

    private static PaletteOverride? CreatePaletteOverride(
        WorldSession.EntitySpawn spawn)
    {
        if (spawn.SubPalettes is not { Count: > 0 } subPalettes)
            return null;

        var ranges = new PaletteOverride.SubPaletteRange[subPalettes.Count];
        for (int i = 0; i < subPalettes.Count; i++)
        {
            ranges[i] = new PaletteOverride.SubPaletteRange(
                subPalettes[i].SubPaletteId,
                subPalettes[i].Offset,
                subPalettes[i].Length);
        }
        return new PaletteOverride(spawn.BasePaletteId ?? 0u, ranges);
    }

    private static PartOverride[] CreatePartOverrides(
        IReadOnlyList<CreateObject.AnimPartChange> changes)
    {
        if (changes.Count == 0)
            return [];

        var result = new PartOverride[changes.Count];
        for (int i = 0; i < changes.Count; i++)
            result[i] = new PartOverride(changes[i].PartIndex, changes[i].NewModelId);
        return result;
    }

    private bool ApplyAppearance(
        LiveEntityRecord expectedRecord,
        WorldSession.EntitySpawn spawn,
        LiveEntityAppearanceUpdateState visualUpdate,
        Setup setup,
        float scale,
        Vector3 worldOrigin,
        IReadOnlyList<uint> collisionPartGfxObjIds,
        IReadOnlyList<MeshRef> meshRefs,
        PaletteOverride? paletteOverride,
        IReadOnlyList<PartOverride> partOverrides,
        IReadOnlyList<Matrix4x4> indexedPartTransforms,
        IReadOnlyList<bool> indexedPartAvailable,
        IReadOnlyList<LiveAnimationPartTemplate> animatedPartTemplate,
        LocalBoundsAccumulator bounds,
        ulong expectedCreateIntegrationVersion)
    {
        WorldEntity entity = visualUpdate.Entity;
        if (!_runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion)
            || !ReferenceEquals(expectedRecord.WorldEntity, entity))
        {
            return false;
        }

        LiveEntityAppearanceCollisionUpdate? appearanceCollision =
            LiveEntityAppearanceBinding.PrepareCollision(
                _runtime,
                _collisionBuilder,
                entity,
                setup,
                collisionPartGfxObjIds,
                spawn,
                worldOrigin);

        bool published = _spawnAdapter.OnAppearanceChanged(
            entity,
            meshRefs,
            partOverrides,
            () =>
            {
                _textures.ReleaseOwner(entity.Id);
                entity.ApplyAppearance(meshRefs, paletteOverride, partOverrides);
            },
            () =>
            {
                if (!_runtime.IsCurrentCreateIntegration(
                        expectedRecord,
                        expectedCreateIntegrationVersion)
                    || !ReferenceEquals(expectedRecord.WorldEntity, entity))
                {
                    return;
                }
                if (appearanceCollision is not null)
                {
                    LiveEntityAppearanceBinding.CommitCollision(
                        _runtime,
                        _shadows,
                        appearanceCollision);
                }
                entity.SetIndexedPartPoses(indexedPartTransforms, indexedPartAvailable);
                if (bounds.TryGet(out Vector3 minimum, out Vector3 maximum))
                    entity.SetLocalBounds(minimum, maximum);
                if (visualUpdate.Animation is { } animation)
                {
                    LiveEntityAppearanceBinding.RebindAnimation(
                        animation,
                        entity,
                        setup,
                        scale,
                        animatedPartTemplate,
                        indexedPartAvailable);
                }
                _classification.InvalidateEntity(entity.Id);
                if (_runtime.IsCurrentCreateIntegration(
                        expectedRecord,
                        expectedCreateIntegrationVersion)
                    && expectedRecord.ProjectionKind is LiveEntityProjectionKind.Attached)
                {
                    _equippedChildren.OnSpawn(expectedRecord.Snapshot);
                }
                else
                {
                    _effectPoses.PublishMeshRefs(entity);
                    if (!_runtime.IsCurrentCreateIntegration(
                            expectedRecord,
                            expectedCreateIntegrationVersion))
                    {
                        return;
                    }
                    _equippedChildren.OnPosePublished(spawn.Guid);
                }
            });
        return published
            && _runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion)
            && ReferenceEquals(expectedRecord.WorldEntity, entity);
    }

    private bool MaterializeProjection(
        RuntimeEntityRecord expectedCanonical,
        LiveEntityRecord? retainedRecord,
        WorldSession.EntitySpawn spawn,
        Setup setup,
        MotionResolver.IdleCycle? idleCycle,
        Vector3 worldOrigin,
        Vector3 worldPosition,
        Quaternion rotation,
        float scale,
        IReadOnlyList<uint> collisionPartGfxObjIds,
        IReadOnlyList<MeshRef> meshRefs,
        PaletteOverride? paletteOverride,
        IReadOnlyList<PartOverride> partOverrides,
        IReadOnlyList<Matrix4x4> indexedPartTransforms,
        IReadOnlyList<bool> indexedPartAvailable,
        IReadOnlyList<LiveAnimationPartTemplate> animatedPartTemplate,
        LocalBoundsAccumulator bounds,
        ulong expectedCreateIntegrationVersion,
        bool synchronizeAnimation)
    {
        EntityEffectProfile profile = spawn.Physics is { } physics
            ? EntityEffectProfile.CreateLive(setup, physics)
            : EntityEffectProfile.CreateDatStatic(setup);
        if (retainedRecord is not null
            && !_runtime.TryGetEffectProfile(spawn.Guid, out _))
        {
            _runtime.SetEffectProfile(spawn.Guid, profile);
        }

        bool createdProjection = false;
        LiveEntityMaterializationResidence residence =
            retainedRecord?.MaterializationResidence
            ?? LiveEntityMaterializationResidence.AwaitRuntimePlacement;
        WorldEntity? entity = _runtime.MaterializeLiveEntity(
            expectedCanonical,
            spawn.Position!.Value.LandblockId,
            localId =>
            {
                createdProjection = true;
                var created = new WorldEntity
                {
                    Id = localId,
                    ServerGuid = spawn.Guid,
                    SourceGfxObjOrSetupId = spawn.SetupTableId!.Value,
                    Position = worldPosition,
                    Rotation = rotation,
                    MeshRefs = meshRefs,
                    PaletteOverride = paletteOverride,
                    PartOverrides = partOverrides,
                    ParentCellId = spawn.Position.Value.LandblockId,
                };
                created.SetIndexedPartPoses(indexedPartTransforms, indexedPartAvailable);
                if (bounds.TryGet(out Vector3 minimum, out Vector3 maximum))
                    created.SetLocalBounds(minimum, maximum);
                return created;
            },
            LiveEntityProjectionKind.World,
            initializeProjection: record => record.EffectProfile = profile,
            out LiveEntityRecord? expectedRecord,
            residence);
        if (entity is null
            || expectedRecord is null
            || !_runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion)
            || !ReferenceEquals(expectedRecord.WorldEntity, entity))
        {
            return false;
        }
        if (residence is LiveEntityMaterializationResidence.AwaitRuntimePlacement
            && expectedCanonical.FullCellId != 0u
            && !_runtime.HasActiveInitialCreateResidence(expectedCanonical))
        {
            if (!_runtime.RebucketLiveEntity(
                    spawn.Guid,
                    expectedCanonical.FullCellId)
                || !_runtime.IsCurrentCreateIntegration(
                    expectedRecord,
                    expectedCreateIntegrationVersion)
                || !ReferenceEquals(expectedRecord.WorldEntity, entity))
            {
                return false;
            }
        }

        if (!createdProjection)
        {
            entity.SetPosition(worldPosition);
            entity.Rotation = rotation;
            entity.ParentCellId = spawn.Position.Value.LandblockId;
            entity.ApplyAppearance(meshRefs, paletteOverride, partOverrides);
            entity.SetIndexedPartPoses(indexedPartTransforms, indexedPartAvailable);
            if (bounds.TryGet(out Vector3 minimum, out Vector3 maximum))
                entity.SetLocalBounds(minimum, maximum);
            _effectPoses.PublishMeshRefs(entity);
        }

        bool retainedAnimation = !createdProjection
            && expectedRecord.AnimationRuntime is LiveEntityAnimationState;
        var snapshot = new AcDream.Plugin.Abstractions.WorldEntitySnapshot(
            entity.Id,
            entity.SourceGfxObjOrSetupId,
            entity.Position,
            entity.Rotation);
        _worldState.Add(snapshot);
        _worldEvents.UpsertCurrent(snapshot);
        if (_runtime.TryMarkWorldSpawnPublished(spawn.Guid))
            _worldEvents.FireEntitySpawned(snapshot);

        if (!_runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion)
            || !ReferenceEquals(expectedRecord.WorldEntity, entity))
        {
            return false;
        }

        _equippedChildren.OnWorldEntityRegistered(spawn.Guid);

        if (!_runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion)
            || !ReferenceEquals(expectedRecord.WorldEntity, entity))
        {
            return false;
        }

        if (_collisionBuilder.Build(
                entity,
                setup,
                collisionPartGfxObjIds,
                spawn,
                expectedRecord.ServerGuid,
                expectedRecord.Generation,
                expectedRecord.WorldEntity!,
                expectedRecord.FinalPhysicsState,
                worldOrigin) is { } collision)
        {
            LiveEntityCollisionBuilder.Register(_shadows, collision);
        }

        if (!_runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion)
            || !ReferenceEquals(expectedRecord.WorldEntity, entity))
            return false;
        bool initialResidenceActive =
            _runtime.HasActiveInitialCreateResidence(expectedCanonical);
        if (!initialResidenceActive)
        {
            _projectiles.TryBind(
                expectedRecord,
                setup,
                _gameTime.CurrentScriptTime,
                _origin.CenterX,
                _origin.CenterY);
        }

        if (!_runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion)
            || !ReferenceEquals(expectedRecord.WorldEntity, entity))
        {
            return false;
        }

        RegisterAnimation(
            expectedRecord,
            entity,
            setup,
            spawn,
            idleCycle,
            scale,
            animatedPartTemplate,
            indexedPartAvailable,
            retainedAnimation,
            synchronizeAnimation);

        if (!_runtime.IsCurrentCreateIntegration(
                expectedRecord,
                expectedCreateIntegrationVersion)
            || !ReferenceEquals(expectedRecord.WorldEntity, entity))
        {
            return false;
        }

        return _runtime.IsCurrentCreateIntegration(
            expectedRecord,
            expectedCreateIntegrationVersion);
    }

    private void RegisterAnimation(
        LiveEntityRecord expectedRecord,
        WorldEntity entity,
        Setup setup,
        WorldSession.EntitySpawn spawn,
        MotionResolver.IdleCycle? idleCycle,
        float scale,
        IReadOnlyList<LiveAnimationPartTemplate> partTemplate,
        IReadOnlyList<bool> partAvailability,
        bool retainedAnimation,
        bool synchronizeAnimation)
    {
        if (retainedAnimation
            && synchronizeAnimation
            && expectedRecord.AnimationRuntime is LiveEntityAnimationState retained)
        {
            SynchronizeRetainedAnimation(
                expectedRecord,
                retained,
                setup,
                spawn,
                idleCycle);
        }
        if (!retainedAnimation
            && idleCycle is not null
            && idleCycle.Framerate != 0f
            && idleCycle.HighFrame > idleCycle.LowFrame
            && idleCycle.Animation.PartFrames.Count > 1)
        {
            AnimationSequencer? sequencer = CreateMotionSequencer(setup, spawn);
            _runtime.SetAnimationRuntime(
                spawn.Guid,
                new LiveEntityAnimationState
                {
                    Entity = entity,
                    Setup = setup,
                    Animation = idleCycle.Animation,
                    LowFrame = Math.Max(0, idleCycle.LowFrame),
                    HighFrame = Math.Min(
                        idleCycle.HighFrame,
                        idleCycle.Animation.PartFrames.Count - 1),
                    Framerate = idleCycle.Framerate,
                    Scale = scale,
                    PartTemplate = partTemplate,
                    PartAvailability = partAvailability,
                    CurrFrame = idleCycle.LowFrame,
                    Sequencer = sequencer,
                });
        }
        else if (!retainedAnimation)
        {
            uint motionTableId = spawn.MotionTableId ?? (uint)setup.DefaultMotionTable;
            if (motionTableId != 0
                && _dats.Get<MotionTable>(motionTableId) is { } motionTable)
            {
                AnimationSequencer sequencer = SpawnMotionInitializer.Create(
                    setup,
                    motionTable,
                    _animationLoader,
                    spawn.MotionState);
                _runtime.SetAnimationRuntime(
                    spawn.Guid,
                    new LiveEntityAnimationState
                    {
                        Entity = entity,
                        Setup = setup,
                        Animation = null!,
                        LowFrame = 0,
                        HighFrame = 0,
                        Framerate = 0f,
                        Scale = scale,
                        PartTemplate = partTemplate,
                        PartAvailability = partAvailability,
                        CurrFrame = 0,
                        Sequencer = sequencer,
                    });

                if (PhysicsDiagnostics.ProbeBuildingEnabled)
                {
                    var initial = SpawnMotionInitializer.ResolvePlan(
                        motionTable,
                        spawn.MotionState);
                    Console.WriteLine(
                        $"[reactive-anim] registered guid=0x{spawn.Guid:X8} "
                        + $"entityId=0x{entity.Id:X8} mtable=0x{motionTableId:X8} "
                        + $"initialStyle=0x{initial.Style:X8} initialCycle=0x{initial.Motion:X8}");
                }
            }
        }

        bool physicsStatic = (expectedRecord.FinalPhysicsState
            & PhysicsStateFlags.Static) != 0;
        if (!retainedAnimation
            && expectedRecord.AnimationRuntime is null
            && (uint)setup.DefaultAnimation != 0)
        {
            var sequencer = new AnimationSequencer(
                setup,
                new MotionTable(),
                _animationLoader);
            if (sequencer.HasCurrentNode)
            {
                _runtime.SetAnimationRuntime(
                    spawn.Guid,
                    new LiveEntityAnimationState
                    {
                        Entity = entity,
                        Setup = setup,
                        Animation = null!,
                        LowFrame = 0,
                        HighFrame = 0,
                        Framerate = 0f,
                        Scale = scale,
                        PartTemplate = partTemplate,
                        PartAvailability = partAvailability,
                        CurrFrame = 0,
                        Sequencer = sequencer,
                    });
            }
        }

        if (expectedRecord.AnimationRuntime is not LiveEntityAnimationState animation)
            return;

        _animationPresenter.PrepareAnimation(expectedRecord, animation);
        if (!physicsStatic
            || animation.Sequencer is not { } staticSequencer
            || spawn.Position is not { } staticPosition)
        {
            return;
        }

        if (_runtime.HasActiveInitialCreateResidence(expectedRecord.Canonical))
        {
            return;
        }

        PhysicsBody body = _runtime.GetOrCreatePhysicsBody(
            spawn.Guid,
            incarnation =>
            {
                var created = new PhysicsBody { Orientation = entity.Rotation };
                created.SnapToCell(
                    staticPosition.LandblockId,
                    entity.Position,
                    new Vector3(
                        staticPosition.PositionX,
                        staticPosition.PositionY,
                        staticPosition.PositionZ));
                return created;
            });
        _staticAnimations.BindLiveOwner(
            entity,
            animation,
            body);
    }

    private void SynchronizeRetainedAnimation(
        LiveEntityRecord expectedRecord,
        LiveEntityAnimationState animation,
        Setup setup,
        WorldSession.EntitySpawn spawn,
        MotionResolver.IdleCycle? idleCycle)
    {
        uint motionTableId = spawn.MotionTableId ?? (uint)setup.DefaultMotionTable;
        MotionTable? motionTable = motionTableId == 0
            ? null
            : _dats.Get<MotionTable>(motionTableId);
        LiveEntityCreateAnimationSynchronization
            .TrySynchronizeInterruptedInitialOwner(
                expectedRecord,
                animation,
                idleCycle?.Animation,
                idleCycle is null ? 0 : Math.Max(0, idleCycle.LowFrame),
                idleCycle is null
                    ? 0
                    : Math.Min(
                        idleCycle.HighFrame,
                        idleCycle.Animation.PartFrames.Count - 1),
                idleCycle?.Framerate ?? 0f,
                motionTable,
                spawn.MotionState);
    }

    private AnimationSequencer? CreateMotionSequencer(
        Setup setup,
        WorldSession.EntitySpawn spawn)
    {
        uint motionTableId = spawn.MotionTableId ?? (uint)setup.DefaultMotionTable;
        return motionTableId != 0
            && _dats.Get<MotionTable>(motionTableId) is { } motionTable
            ? SpawnMotionInitializer.Create(
                setup,
                motionTable,
                _animationLoader,
                spawn.MotionState)
            : null;
    }

    private static bool HasHumanoidNullPartLayout(Setup setup)
    {
        if (setup.Parts.Count != 34)
            return false;
        const uint nullPartGfx = 0x010001ECu;
        int nullSlots = 0;
        for (int i = 17; i < setup.Parts.Count; i++)
        {
            if ((uint)setup.Parts[i] == nullPartGfx)
                nullSlots++;
        }
        return nullSlots >= 8;
    }

    private void ProbeWorldFrameAgreement(uint landblockId)
    {
        uint runtimeCenter = _runtime.Physics.WorldFrameCenterLandblockId;
        if (runtimeCenter == 0u || !_origin.IsKnown)
            return;

        int runtimeCenterX = (int)((runtimeCenter >> 24) & 0xFFu);
        int runtimeCenterY = (int)((runtimeCenter >> 16) & 0xFFu);
        Console.WriteLine(System.FormattableString.Invariant(
            $"[world-frame] agree centre=({runtimeCenterX},{runtimeCenterY}) projecting=0x{landblockId:X8}"));
    }
}
