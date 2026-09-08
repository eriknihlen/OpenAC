using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using DatReaderWriter.Types;
using Xunit;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;

namespace AcDream.App.Tests.Rendering.Vfx;

public sealed class EntityScriptActivatorTests
{
    /// <summary>Recording sink so we can assert which hooks the runner fires.</summary>
    private sealed class RecordingSink : IAnimationHookSink
    {
        public List<(uint EntityId, Vector3 Pos, AnimationHook Hook)> Calls = new();
        public void OnHook(uint entityId, Vector3 worldPos, AnimationHook hook)
            => Calls.Add((entityId, worldPos, hook));
    }

    private static DatPhysicsScript BuildScript(params (double time, AnimationHook hook)[] items)
    {
        var script = new DatPhysicsScript();
        foreach (var (t, h) in items)
            script.ScriptData.Add(new PhysicsScriptData { StartTime = t, Hook = h });
        return script;
    }

    private static WorldEntity MakeEntity(uint serverGuid, Vector3 position) =>
        new()
        {
            Id = serverGuid,
            ServerGuid = serverGuid,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = position,
            Rotation = Quaternion.Identity,
            MeshRefs = System.Array.Empty<MeshRef>(),
        };

    private record Pipeline(
        ParticleSystem System,
        ParticleHookSink Sink,
        EntityEffectPoseRegistry Poses,
        PhysicsScriptRunner Runner,
        RecordingSink Recording);

    private static Pipeline BuildPipeline(params (uint id, DatPhysicsScript script)[] scripts)
    {
        var registry = new EmitterDescRegistry();
        var system = new ParticleSystem(registry);
        var poses = new EntityEffectPoseRegistry();
        var hookSink = new ParticleHookSink(system, poses);   // for activator's StopAllForEntity
        var recording = new RecordingSink();                  // for runner's hook dispatch
        var table = new Dictionary<uint, DatPhysicsScript>();
        foreach (var (id, s) in scripts) table[id] = s;
        var runner = new PhysicsScriptRunner(
            id => table.TryGetValue(id, out var s) ? s : null,
            recording);
        return new Pipeline(system, hookSink, poses, runner, recording);
    }

    private static System.Func<WorldEntity, ScriptActivationInfo?> StaticResolver(uint scriptId)
        => _ => new ScriptActivationInfo(scriptId, System.Array.Empty<Matrix4x4>());

    [Fact]
    public void OnCreate_WithDefaultScript_FiresRunnerWithEntityGuidAndPosition()
    {
        var p = BuildPipeline(
            (0xAAu, BuildScript((0.0, new CreateParticleHook { EmitterInfoId = 100 }))));
        var activator = new EntityScriptActivator(p.Runner, p.Sink, p.Poses, StaticResolver(0xAAu));
        var entity = MakeEntity(serverGuid: 0xCAFEu, position: new Vector3(1, 2, 3));

        activator.OnCreate(entity);

        Assert.Equal(1, p.Runner.ActiveScriptCount);
        p.Runner.Tick(0.001f);
        Assert.Single(p.Recording.Calls);
        Assert.Equal(0xCAFEu, p.Recording.Calls[0].EntityId);
        Assert.Equal(new Vector3(1, 2, 3), p.Recording.Calls[0].Pos);
    }

    [Fact]
    public void OnCreate_WithoutDefaultScript_DoesNothing()
    {
        var p = BuildPipeline();   // no scripts registered
        var activator = new EntityScriptActivator(p.Runner, p.Sink, p.Poses, _ => null);
        var entity = MakeEntity(serverGuid: 0xCAFEu, position: Vector3.Zero);

        activator.OnCreate(entity);

        Assert.Equal(0, p.Runner.ActiveScriptCount);
        Assert.Empty(p.Recording.Calls);
    }

    /// <summary>
    /// Persistent emitter: TotalDuration=0 and TotalParticles=0 prevent
    /// auto-finish; InitialParticles=1 ensures a particle spawns at t=0
    /// without waiting for the Birthrate timer; Lifespan=999f keeps that
    /// particle alive far past the test horizon.
    /// </summary>
    private static EmitterDesc BuildPersistentEmitterDesc() => new()
    {
        DatId          = 100u,
        Type           = ParticleType.Still,
        EmitterKind    = ParticleEmitterKind.BirthratePerSec,
        MaxParticles   = 4,
        InitialParticles = 1,
        TotalParticles = 0,
        TotalDuration  = 0f,  // 0 = no time-based finish
        Lifespan       = 999f,
        LifetimeMin    = 999f,
        LifetimeMax    = 999f,
        Birthrate      = 0.5f,
        StartSize      = 0.5f,
        EndSize        = 0.5f,
        StartAlpha     = 1f,
        EndAlpha       = 1f,
    };

    [Fact]
    public void OnCreate_PublishesEntityRotationForHookOffsetTransform()
    {

        var registry = new EmitterDescRegistry();
        registry.Register(BuildPersistentEmitterDesc());

        var system = new ParticleSystem(registry);
        var poses = new EntityEffectPoseRegistry();
        var hookSink = new ParticleHookSink(system, poses);

        // Hook offset = (1, 0, 0) in entity-local frame.
        var hookOffset = new Frame
        {
            Origin = new Vector3(1f, 0f, 0f),
            Orientation = Quaternion.Identity,
        };
        var script = BuildScript(
            (0.0, new CreateParticleHook
            {
                EmitterInfoId = 100u,
                Offset = hookOffset,
                PartIndex = uint.MaxValue,
            }));
        var table = new Dictionary<uint, DatPhysicsScript> { [0xAAu] = script };
        var runner = new PhysicsScriptRunner(
            id => table.TryGetValue(id, out var s) ? s : null,
            hookSink);

        var activator = new EntityScriptActivator(runner, hookSink, poses, StaticResolver(0xAAu));

        // Entity rotated 90° around world-Z (yaw left); local +X maps to world +Y.
        var entityRotation = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ, MathF.PI / 2f);
        var entity = new WorldEntity
        {
            Id = 0xCAFEu,
            ServerGuid = 0xCAFEu,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = entityRotation,
            MeshRefs = System.Array.Empty<MeshRef>(),
        };

        activator.OnCreate(entity);
        runner.Tick(0.001f);
        system.Tick(0.001f);

        // Find the live particle. With the rotation applied, world position of
        // the local-(1,0,0) offset should be approximately world-(0,1,0). Without
        // the rotation seed (the bug), it would be world-(1,0,0).
        var live = system.EnumerateLive().FirstOrDefault();
        Assert.NotNull(live.Emitter);
        var worldPos = live.Emitter.Particles[live.Index].Position;
        Assert.InRange(worldPos.X, -0.01f, 0.01f);
        Assert.InRange(worldPos.Y,  0.99f, 1.01f);
    }

    [Fact]
    public void OnRemove_StopsScriptsAndEmitters()
    {
        var registry = new EmitterDescRegistry();
        registry.Register(BuildPersistentEmitterDesc());

        var system  = new ParticleSystem(registry);
        var poses = new EntityEffectPoseRegistry();
        var hookSink = new ParticleHookSink(system, poses);

        var script = BuildScript((0.0, new CreateParticleHook
        {
            EmitterInfoId = 100u,
            Offset = new Frame(),
            PartIndex = uint.MaxValue,
        }));
        var table  = new Dictionary<uint, DatPhysicsScript> { [0xAAu] = script };
        var runner = new PhysicsScriptRunner(
            id => table.TryGetValue(id, out var s) ? s : null,
            hookSink);  // runner dispatches into real sink, not RecordingSink

        var activator = new EntityScriptActivator(runner, hookSink, poses, StaticResolver(0xAAu));
        var entity = MakeEntity(serverGuid: 0xCAFEu, position: Vector3.Zero);

        activator.OnCreate(entity);
        runner.Tick(0.001f);

        Assert.True(system.ActiveEmitterCount > 0,
            "Setup precondition failed: emitter should be alive after the hook fires.");

        activator.OnRemove(entity);

        Assert.Equal(0, runner.ActiveScriptCount);
        // sink.StopAllForEntity marks the emitter Finished; system.Tick reaps it.
        system.Tick(0.01f);
        Assert.Equal(0, system.ActiveEmitterCount);
    }

    [Fact]
    public void OnCreate_UsesCanonicalStaticIdentity_WhenServerGuidZero()
    {
        // C.1.5b: dat-hydrated EnvCell statics + exterior stabs have
        // ServerGuid == 0 but a stable entity.Id in the 0x40xxxxxx range.
        // OnCreate must use entity.Id as the key (not skip).
        var p = BuildPipeline(
            (0xAAu, BuildScript((0.0, new CreateParticleHook { EmitterInfoId = 100 }))));
        var activator = new EntityScriptActivator(p.Runner, p.Sink, p.Poses, StaticResolver(0xAAu));
        var entity = new WorldEntity
        {
            Id = 0x40A9B401u,                  // dat-hydrated interior id
            ServerGuid = 0u,                   // no server guid
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = new Vector3(5, 5, 5),
            Rotation = Quaternion.Identity,
            MeshRefs = System.Array.Empty<MeshRef>(),
        };

        activator.OnCreate(entity);

        Assert.Equal(1, p.Runner.ActiveScriptCount);
        p.Runner.Tick(0.001f);
        Assert.Single(p.Recording.Calls);
        Assert.Equal(entity.Id, p.Recording.Calls[0].EntityId);
        Assert.Equal(new Vector3(5, 5, 5), p.Recording.Calls[0].Pos);
    }

    [Fact]
    public void DatStaticAnimationOwner_RegistersAndUnregistersExactlyOnce()
    {
        var p = BuildPipeline();
        int registerCount = 0;
        int unregisterCount = 0;
        WorldEntity? registeredEntity = null;
        ScriptActivationInfo info = new(
            ScriptId: 0,
            PartTransforms: Array.Empty<Matrix4x4>(),
            DefaultAnimationId: 0x03000001u,
            UsesStaticAnimationWorkset: true);
        var activator = new EntityScriptActivator(
            p.Runner,
            p.Sink,
            p.Poses,
            _ => info,
            registerDatStaticAnimationOwner: (entity, actual) =>
            {
                registerCount++;
                registeredEntity = entity;
                Assert.Same(info, actual);
                return true;
            },
            unregisterDatStaticAnimationOwner: ownerId =>
            {
                unregisterCount++;
                Assert.Equal(0x40A9B420u, ownerId);
            });
        WorldEntity entity = MakeDatStatic(0x40A9B420u, Vector3.One);

        activator.OnCreate(entity);
        activator.OnCreate(entity);
        activator.OnRemove(entity);
        activator.OnRemove(entity);

        Assert.Equal(1, registerCount);
        Assert.Equal(1, unregisterCount);
        Assert.Same(entity, registeredEntity);
    }

    [Fact]
    public void LiveNonStaticEntity_DoesNotEnterStaticAnimationWorkset()
    {
        var p = BuildPipeline();
        int registerCount = 0;
        var activator = new EntityScriptActivator(
            p.Runner,
            p.Sink,
            p.Poses,
            _ => new ScriptActivationInfo(
                ScriptId: 0,
                PartTransforms: Array.Empty<Matrix4x4>(),
                DefaultAnimationId: 0x03000001u),
            registerDatStaticAnimationOwner: (_, _) =>
            {
                registerCount++;
                return true;
            });

        activator.OnCreate(MakeEntity(0x70000420u, Vector3.Zero));

        Assert.Equal(0, registerCount);
    }

    [Fact]
    public void LivePhysicsStaticEntity_EntersAndLeavesStaticAnimationWorkset()
    {
        var p = BuildPipeline();
        int registerCount = 0;
        int unregisterCount = 0;
        var activator = new EntityScriptActivator(
            p.Runner,
            p.Sink,
            p.Poses,
            _ => new ScriptActivationInfo(
                ScriptId: 0,
                PartTransforms: Array.Empty<Matrix4x4>(),
                DefaultAnimationId: 0x03000001u,
                UsesStaticAnimationWorkset: true),
            registerDatStaticAnimationOwner: (_, _) =>
            {
                registerCount++;
                return true;
            },
            unregisterDatStaticAnimationOwner: _ => unregisterCount++);
        WorldEntity entity = MakeEntity(0x70000421u, Vector3.Zero);

        activator.OnCreate(entity);
        activator.OnRemove(entity);

        Assert.Equal(1, registerCount);
        Assert.Equal(1, unregisterCount);
    }

    [Fact]
    public void DatStaticActivation_RetriesOnlyIncompleteAcquisitionStages()
    {
        var p = BuildPipeline(
            (0xAAu, BuildScript((10.0, new CreateParticleHook { EmitterInfoId = 100 }))));
        WorldEntity entity = MakeDatStatic(0x40A9B421u, Vector3.Zero);
        var setup = new DatReaderWriter.DBObjs.Setup();
        ScriptActivationInfo info = new(
            ScriptId: 0xAAu,
            PartTransforms: Array.Empty<Matrix4x4>(),
            EffectProfile: EntityEffectProfile.CreateDatStatic(setup),
            Setup: setup,
            DefaultAnimationId: 0x03000001u,
            UsesStaticAnimationWorkset: true);
        int effectAttempts = 0;
        int animationAttempts = 0;
        var activator = new EntityScriptActivator(
            p.Runner,
            p.Sink,
            p.Poses,
            _ => info,
            registerDatStaticEffectOwner: (_, _, _) =>
            {
                effectAttempts++;
                if (effectAttempts == 1)
                    throw new InvalidOperationException("effect acquire fault");
            },
            registerDatStaticAnimationOwner: (_, _) =>
            {
                animationAttempts++;
                if (animationAttempts == 1)
                    throw new InvalidOperationException("animation acquire fault");
                return true;
            });

        Assert.Throws<InvalidOperationException>(() => activator.OnCreate(entity));
        Assert.Equal(1, effectAttempts);
        Assert.Equal(0, animationAttempts);
        Assert.Equal(0, p.Runner.ActiveOwnerCount);

        Assert.Throws<InvalidOperationException>(() => activator.OnCreate(entity));
        Assert.Equal(2, effectAttempts);
        Assert.Equal(1, animationAttempts);
        Assert.Equal(0, p.Runner.ActiveOwnerCount);

        activator.OnCreate(entity);
        activator.OnCreate(entity);
        Assert.Equal(2, effectAttempts);
        Assert.Equal(2, animationAttempts);
        Assert.Equal(1, p.Runner.ActiveOwnerCount);
    }

    [Fact]
    public void DatStaticRemoval_RetriesOnlyIncompleteReleaseStages()
    {
        var p = BuildPipeline();
        WorldEntity entity = MakeDatStatic(0x40A9B422u, Vector3.Zero);
        var setup = new DatReaderWriter.DBObjs.Setup();
        ScriptActivationInfo info = new(
            ScriptId: 0,
            PartTransforms: Array.Empty<Matrix4x4>(),
            EffectProfile: EntityEffectProfile.CreateDatStatic(setup),
            Setup: setup,
            DefaultAnimationId: 0x03000001u,
            UsesStaticAnimationWorkset: true);
        int animationReleaseAttempts = 0;
        int effectReleaseAttempts = 0;
        var activator = new EntityScriptActivator(
            p.Runner,
            p.Sink,
            p.Poses,
            _ => info,
            registerDatStaticEffectOwner: (_, _, _) => { },
            unregisterDatStaticEffectOwner: _ =>
            {
                effectReleaseAttempts++;
                if (effectReleaseAttempts == 1)
                    throw new InvalidOperationException("effect release fault");
            },
            registerDatStaticAnimationOwner: (_, _) => true,
            unregisterDatStaticAnimationOwner: _ =>
            {
                animationReleaseAttempts++;
                if (animationReleaseAttempts == 1)
                    throw new InvalidOperationException("animation release fault");
            });
        activator.OnCreate(entity);

        Assert.Throws<InvalidOperationException>(() => activator.OnRemove(entity));
        Assert.Equal(1, animationReleaseAttempts);
        Assert.Equal(0, effectReleaseAttempts);

        Assert.Throws<InvalidOperationException>(() => activator.OnRemove(entity));
        Assert.Equal(2, animationReleaseAttempts);
        Assert.Equal(1, effectReleaseAttempts);

        activator.OnRemove(entity);
        activator.OnRemove(entity);
        Assert.Equal(2, animationReleaseAttempts);
        Assert.Equal(2, effectReleaseAttempts);
    }

    [Fact]
    public void CollidingDatEntityIds_FailFastAtAllocatorBoundary()
    {
        var p = BuildPipeline(
            (0xAAu, BuildScript((1.0, new CreateParticleHook { EmitterInfoId = 100 }))));
        var activator = new EntityScriptActivator(p.Runner, p.Sink, p.Poses, StaticResolver(0xAAu));
        WorldEntity first = MakeDatStatic(0x80A9B400u, new Vector3(1, 0, 0));
        WorldEntity second = MakeDatStatic(0x80A9B400u, new Vector3(2, 0, 0));

        activator.OnCreate(first);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => activator.OnCreate(second));

        Assert.Contains("globally unique", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, p.Runner.ActiveOwnerCount);
    }

    [Fact]
    public void DatStaticRebind_RetriesOnlyUnfinishedOwnersAndDoesNotReplayDefault()
    {
        var p = BuildPipeline(
            (0xAAu, BuildScript((1.0, new CreateParticleHook { EmitterInfoId = 100 }))));
        var setup = new DatReaderWriter.DBObjs.Setup();
        ScriptActivationInfo info = new(
            ScriptId: 0xAAu,
            PartTransforms: Array.Empty<Matrix4x4>(),
            EffectProfile: EntityEffectProfile.CreateDatStatic(setup),
            Setup: setup,
            DefaultAnimationId: 0x03000001u,
            UsesStaticAnimationWorkset: true);
        int effectUpdates = 0;
        int animationRegistrations = 0;
        int animationRebindAttempts = 0;
        var activator = new EntityScriptActivator(
            p.Runner,
            p.Sink,
            p.Poses,
            _ => info,
            registerDatStaticEffectOwner: (_, _, _) => effectUpdates++,
            registerDatStaticAnimationOwner: (_, _) =>
            {
                animationRegistrations++;
                return true;
            },
            rebindDatStaticAnimationOwner: (_, _) =>
            {
                animationRebindAttempts++;
                if (animationRebindAttempts == 1)
                    throw new InvalidOperationException("animation rebind fault");
            });
        WorldEntity first = MakeDatStatic(0x80A9B400u, Vector3.One);
        WorldEntity replacement = MakeDatStatic(
            first.Id,
            new Vector3(7f, 8f, 9f));
        activator.OnCreate(first);

        Assert.Throws<InvalidOperationException>(() =>
            activator.OnRebindStatic(first, replacement));
        activator.OnRebindStatic(first, replacement);
        activator.OnCreate(replacement);

        Assert.Equal(1, p.Runner.ActiveOwnerCount);
        Assert.Equal(2, effectUpdates);
        Assert.Equal(1, animationRegistrations);
        Assert.Equal(2, animationRebindAttempts);
        Assert.True(p.Poses.TryGetRootPose(first.Id, out Matrix4x4 root));
        Assert.Equal(replacement.Position, root.Translation);
        p.Runner.Tick(1.1);
        Assert.Single(p.Recording.Calls);
        Assert.Equal(replacement.Position, p.Recording.Calls[0].Pos);
    }

    [Fact]
    public void DatStaticRebind_MissingDefaultAnimationNeverClaimsWorksetOwner()
    {
        var p = BuildPipeline();
        var setup = new DatReaderWriter.DBObjs.Setup();
        ScriptActivationInfo info = new(
            ScriptId: 0,
            PartTransforms: Array.Empty<Matrix4x4>(),
            Setup: setup,
            DefaultAnimationId: 0x03000001u,
            UsesStaticAnimationWorkset: true);
        int registerAttempts = 0;
        int rebindAttempts = 0;
        var activator = new EntityScriptActivator(
            p.Runner,
            p.Sink,
            p.Poses,
            _ => info,
            registerDatStaticAnimationOwner: (_, _) =>
            {
                registerAttempts++;
                return false;
            },
            rebindDatStaticAnimationOwner: (_, _) => rebindAttempts++);
        WorldEntity first = MakeDatStatic(0x80A9B400u, Vector3.Zero);
        WorldEntity replacement = MakeDatStatic(first.Id, Vector3.One);

        activator.OnCreate(first);
        activator.OnRebindStatic(first, replacement);
        activator.OnCreate(replacement);

        Assert.Equal(2, registerAttempts);
        Assert.Equal(0, rebindAttempts);
        Assert.True(p.Poses.TryGetRootPose(first.Id, out _));
    }

    [Fact]
    public void LiveParticleFilterUsesCanonicalLocalEffectOwnerNotServerGuid()
    {
        var p = BuildPipeline(
            (0xAAu, BuildScript((0.0, new CreateParticleHook { EmitterInfoId = 100 }))));
        var activator = new EntityScriptActivator(
            p.Runner,
            p.Sink,
            p.Poses,
            StaticResolver(0xAAu));
        var entity = new WorldEntity
        {
            Id = 1_000_123u,
            ServerGuid = 0x70000001u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };

        activator.OnCreate(entity);
        p.Runner.Tick(0.001f);

        Assert.Single(p.Recording.Calls);
        Assert.Equal(entity.Id, p.Recording.Calls[0].EntityId);
        Assert.NotEqual(entity.ServerGuid, p.Recording.Calls[0].EntityId);
    }

    [Fact]
    public void RemovingLiveOwner_DoesNotClearIndependentDatStaticPose()
    {
        var p = BuildPipeline();
        var activator = new EntityScriptActivator(
            p.Runner,
            p.Sink,
            p.Poses,
            StaticResolver(0));
        WorldEntity datStatic = MakeDatStatic(0x40A9B410u, Vector3.One);
        var live = new WorldEntity
        {
            Id = 1_000_410u,
            ServerGuid = 0x70000410u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = new Vector3(2, 2, 2),
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        activator.OnCreate(datStatic);
        activator.OnCreate(live);

        activator.OnRemove(live);

        Assert.True(p.Poses.TryGetRootPose(datStatic.Id, out _));
        Assert.False(p.Poses.TryGetRootPose(live.Id, out _));
    }

    [Fact]
    public void OnCreate_PassesPartTransformsToSink()
    {
        var registry = new EmitterDescRegistry();
        registry.Register(BuildPersistentEmitterDesc());
        var system = new ParticleSystem(registry);
        var poses = new EntityEffectPoseRegistry();
        var hookSink = new ParticleHookSink(system, poses);

        var hookOffset = new Frame { Origin = new Vector3(1f, 0, 0), Orientation = Quaternion.Identity };
        var script = BuildScript(
            (0.0, new CreateParticleHook { EmitterInfoId = 100u, Offset = hookOffset, PartIndex = 1 }));
        var table = new Dictionary<uint, DatPhysicsScript> { [0xAAu] = script };
        var runner = new PhysicsScriptRunner(
            id => table.TryGetValue(id, out var s) ? s : null,
            hookSink);

        var partTransforms = new Matrix4x4[]
        {
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(0f, 0f, 1f),
        };

        var activator = new EntityScriptActivator(runner, hookSink, poses,
            _ => new ScriptActivationInfo(0xAAu, partTransforms));
        var entity = MakeEntity(serverGuid: 0xCAFEu, position: Vector3.Zero);

        activator.OnCreate(entity);
        runner.Tick(0.001f);
        system.Tick(0.001f);

        var live = system.EnumerateLive().FirstOrDefault();
        Assert.NotNull(live.Emitter);
        var pos = live.Emitter.Particles[live.Index].Position;
        Assert.InRange(pos.X, 0.99f, 1.01f);
        Assert.InRange(pos.Y, -0.01f, 0.01f);
        Assert.InRange(pos.Z, 0.99f, 1.01f);
    }

    [Fact]
    public void OnRemove_StopsReservedOwner_ForDatHydratedEntity()
    {
        var registry = new EmitterDescRegistry();
        registry.Register(BuildPersistentEmitterDesc());
        var system = new ParticleSystem(registry);
        var poses = new EntityEffectPoseRegistry();
        var hookSink = new ParticleHookSink(system, poses);

        var script = BuildScript((0.0, new CreateParticleHook
        {
            EmitterInfoId = 100u,
            Offset = new Frame(),
            PartIndex = uint.MaxValue,
        }));
        var table = new Dictionary<uint, DatPhysicsScript> { [0xAAu] = script };
        var runner = new PhysicsScriptRunner(
            id => table.TryGetValue(id, out var s) ? s : null,
            hookSink);

        var activator = new EntityScriptActivator(runner, hookSink, poses, StaticResolver(0xAAu));
        var entity = new WorldEntity
        {
            Id = 0x40A9B402u,
            ServerGuid = 0u,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = System.Array.Empty<MeshRef>(),
        };

        activator.OnCreate(entity);
        runner.Tick(0.001f);
        Assert.True(system.ActiveEmitterCount > 0);

        activator.OnRemove(entity);

        Assert.Equal(0, runner.ActiveScriptCount);
        system.Tick(0.01f);
        Assert.Equal(0, system.ActiveEmitterCount);
    }

    private static WorldEntity MakeDatStatic(uint id, Vector3 position) => new()
    {
        Id = id,
        ServerGuid = 0,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = position,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

}
