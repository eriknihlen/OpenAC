using System.Numerics;
using AcDream.Core.Vfx;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Vfx;

public sealed class ParticleHookSinkTests
{
    private const uint Owner = 0xCAFEu;

    private static EmitterDesc MakeDesc(
        uint id,
        bool attachLocal,
        int totalParticles = 0,
        float totalDuration = 0f) =>
        new()
        {
            DatId = id,
            Type = ParticleType.Still,
            Flags = EmitterFlags.Billboard | (attachLocal ? EmitterFlags.AttachLocal : 0),
            EmitterKind = ParticleEmitterKind.BirthratePerSec,
            MaxParticles = 4,
            InitialParticles = 1,
            TotalParticles = totalParticles,
            TotalDuration = totalDuration,
            LifetimeMin = 0.05f,
            LifetimeMax = 0.05f,
            Lifespan = 0.05f,
            StartSize = 1f,
            EndSize = 1f,
            StartAlpha = 1f,
            EndAlpha = 1f,
            Birthrate = 1000f,
        };

    private static (ParticleSystem System, ParticleHookSink Sink, MutablePoseSource Poses) Harness(
        uint emitterId,
        bool attachLocal = false,
        int totalParticles = 0,
        float totalDuration = 0f)
    {
        var registry = new EmitterDescRegistry();
        registry.Register(MakeDesc(emitterId, attachLocal, totalParticles, totalDuration));
        var system = new ParticleSystem(registry, new Random(42));
        var poses = new MutablePoseSource();
        poses.Publish(Owner, Matrix4x4.Identity, Matrix4x4.Identity);
        return (system, new ParticleHookSink(system, poses), poses);
    }

    private static CreateParticleHook Create(uint emitterInfoId, uint logicalId, int partIndex = 0) =>
        new()
        {
            EmitterInfoId = emitterInfoId,
            EmitterId = logicalId,
            PartIndex = unchecked((uint)partIndex),
            Offset = new Frame(),
        };

    [Fact]
    public void RefreshAttachedEmitters_WithAttachLocal_MovesExistingParticleToCurrentPose()
    {
        const uint emitterId = 0x32000010u;
        var (system, sink, poses) = Harness(emitterId, attachLocal: true);
        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, 0));
        system.Tick(0.01f);
        Assert.Equal(Vector3.Zero, system.EnumerateLive().Single().Emitter.Particles[0].Position);

        poses.Publish(Owner, Matrix4x4.CreateTranslation(5, 7, 0), Matrix4x4.Identity);
        sink.RefreshAttachedEmitters();
        system.Tick(0.01f);

        Assert.Equal(new Vector3(5, 7, 0),
            system.EnumerateLive().Single().Emitter.Particles[0].Position);
    }

    [Fact]
    public void WorldReleasedParticles_KeepBirthPosition_WhileNewAnchorMoves()
    {
        const uint emitterId = 0x32000011u;
        var (system, sink, poses) = Harness(emitterId, attachLocal: false);
        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, 0));
        system.Tick(0.01f);

        poses.Publish(Owner, Matrix4x4.CreateTranslation(9, 0, 0), Matrix4x4.Identity);
        sink.RefreshAttachedEmitters();
        system.Tick(0.01f);

        Assert.Equal(Vector3.Zero,
            system.EnumerateLive().Single().Emitter.Particles[0].Position);
    }

    [Fact]
    public void Spawn_UsesCurrentIndexedPartAndRoot_WithRowVectorComposition()
    {
        const uint emitterId = 0x32000030u;
        var (system, sink, poses) = Harness(emitterId);
        Matrix4x4 root = Matrix4x4.CreateRotationZ(MathF.PI / 2f)
            * Matrix4x4.CreateTranslation(10, 20, 30);
        poses.Publish(
            Owner,
            root,
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(0, 0, 2));

        var hook = Create(emitterId, 0, partIndex: 1);
        hook.Offset = new Frame
        {
            Origin = new Vector3(1, 0, 0),
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.1f),
        };
        sink.OnHook(Owner, Vector3.Zero, hook);
        system.Tick(0.001f);

        Vector3 position = system.EnumerateLive().Single().Emitter.Particles[0].Position;
        Assert.InRange(position.X, 9.99f, 10.01f);
        Assert.InRange(position.Y, 20.99f, 21.01f);
        Assert.InRange(position.Z, 31.99f, 32.01f);
    }

    [Fact]
    public void PartMinusOne_UsesRoot_AndInvalidIndexedPartDoesNotFallback()
    {
        const uint emitterId = 0x32000031u;
        var (system, sink, poses) = Harness(emitterId);
        poses.Publish(
            Owner,
            Matrix4x4.CreateTranslation(3, 4, 5),
            Matrix4x4.CreateTranslation(50, 0, 0));

        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, 0, partIndex: -1));
        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, 0, partIndex: 99));
        system.Tick(0.001f);

        Assert.Single(system.EnumerateLive());
        Assert.Equal(new Vector3(3, 4, 5),
            system.EnumerateLive().Single().Emitter.Particles[0].Position);
    }

    [Fact]
    public void NormalLogicalId_Replaces_BlockingSuppresses_ThenCreatesAfterRetirement()
    {
        const uint emitterId = 0x32000040u;
        const uint logicalId = 0x1234u;
        var (system, sink, _) = Harness(emitterId);
        var normal = Create(emitterId, logicalId);
        var blocking = new RetailCreateBlockingParticleHook
        {
            EmitterInfoId = emitterId,
            EmitterId = logicalId,
            PartIndex = 0,
            Offset = new Frame(),
        };

        sink.OnHook(Owner, Vector3.Zero, normal);
        sink.OnHook(Owner, Vector3.Zero, blocking);
        Assert.Equal(1, system.ActiveEmitterCount);

        sink.OnHook(Owner, Vector3.Zero, normal);
        system.Tick(0.001f);
        Assert.Equal(1, system.ActiveEmitterCount);

        sink.OnHook(Owner, Vector3.Zero,
            new DestroyParticleHook { EmitterId = logicalId });
        system.Tick(0.001f);
        Assert.Equal(0, system.ActiveEmitterCount);

        sink.OnHook(Owner, Vector3.Zero, blocking);
        Assert.Equal(1, system.ActiveEmitterCount);
    }

    [Fact]
    public void BlockingLogicalIdZero_AlwaysCreatesAnonymousEmitter()
    {
        const uint emitterId = 0x32000041u;
        var (system, sink, _) = Harness(emitterId);
        var blocking = new RetailCreateBlockingParticleHook
        {
            EmitterInfoId = emitterId,
            EmitterId = 0,
            PartIndex = 0,
            Offset = new Frame(),
        };

        sink.OnHook(Owner, Vector3.Zero, blocking);
        sink.OnHook(Owner, Vector3.Zero, blocking);
        Assert.Equal(2, system.ActiveEmitterCount);
    }

    [Fact]
    public void StopLogicalEmitter_RemainsBlockingUntilFinalParticleRetires()
    {
        const uint emitterId = 0x32000042u;
        const uint logicalId = 0x777u;
        var (system, sink, _) = Harness(emitterId);
        var blocking = new RetailCreateBlockingParticleHook
        {
            EmitterInfoId = emitterId,
            EmitterId = logicalId,
            PartIndex = 0,
            Offset = new Frame(),
        };

        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, logicalId));
        sink.OnHook(Owner, Vector3.Zero, new StopParticleHook { EmitterId = logicalId });
        sink.OnHook(Owner, Vector3.Zero, blocking);

        Assert.Equal(1, system.ActiveEmitterCount);
    }

    [Fact]
    public void StopLogicalEmitter_NormalCreateStillReplacesIt()
    {
        const uint emitterId = 0x32000043u;
        const uint logicalId = 0x778u;
        var (system, sink, _) = Harness(emitterId);

        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, logicalId));
        sink.OnHook(Owner, Vector3.Zero, new StopParticleHook { EmitterId = logicalId });
        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, logicalId));
        system.Tick(0.001f);

        Assert.Equal(1, system.ActiveEmitterCount);
    }

    [Fact]
    public void NaturalRetirement_PrunesLogicalIdForBlockingReuse()
    {
        const uint emitterId = 0x32000020u;
        const uint logicalId = 0xABCDu;
        var (system, sink, _) = Harness(emitterId, totalParticles: 1);
        var blocking = new RetailCreateBlockingParticleHook
        {
            EmitterInfoId = emitterId,
            EmitterId = logicalId,
            PartIndex = 0,
            Offset = new Frame(),
        };
        sink.OnHook(Owner, Vector3.Zero, blocking);
        for (int i = 0; i < 5; i++)
            system.Tick(0.05f);
        Assert.Equal(0, system.ActiveEmitterCount);

        sink.OnHook(Owner, Vector3.Zero, blocking);
        Assert.Equal(1, system.ActiveEmitterCount);
    }

    [Fact]
    public void MissingEmitterAsset_ReportsDiagnosticWithoutFabricatingEffect()
    {
        const uint registeredId = 0x32000020u;
        const uint missingId = 0x32DEAD00u;
        var (system, sink, _) = Harness(registeredId);
        var diagnostics = new List<string>();
        sink.DiagnosticSink = diagnostics.Add;

        sink.OnHook(Owner, Vector3.Zero, Create(missingId, logicalId: 7u));

        Assert.Equal(0, system.ActiveEmitterCount);
        string diagnostic = Assert.Single(diagnostics);
        Assert.Contains($"0x{missingId:X8}", diagnostic, StringComparison.Ordinal);
        Assert.Contains($"0x{Owner:X8}", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingEmitterAsset_ReportsOnlyOnceAcrossManyOwners()
    {
        const uint missingId = 0x32DEAD00u;
        const uint secondOwner = Owner + 1u;
        var registry = new EmitterDescRegistry();
        var system = new ParticleSystem(registry, new Random(42));
        var poses = new MutablePoseSource();
        poses.Publish(Owner, Matrix4x4.Identity, Matrix4x4.Identity);
        poses.Publish(secondOwner, Matrix4x4.Identity, Matrix4x4.Identity);
        var sink = new ParticleHookSink(system, poses);
        var diagnostics = new List<string>();
        sink.DiagnosticSink = diagnostics.Add;

        sink.OnHook(Owner, Vector3.Zero, Create(missingId, logicalId: 0u));
        sink.OnHook(secondOwner, Vector3.Zero, Create(missingId, logicalId: 0u));
        sink.OnHook(Owner, Vector3.Zero, Create(missingId, logicalId: 0u));

        Assert.Empty(system.EnumerateEmitters());
        Assert.Single(diagnostics);
        Assert.Contains($"0x{missingId:X8}", diagnostics[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MissingLivePose_HidesPresentationWithoutEndingEmitterLifetime()
    {
        const uint emitterId = 0x32000055u;
        var (system, sink, poses) = Harness(emitterId);
        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, logicalId: 11u));
        ParticleEmitter emitter = system.EnumerateLive().Single().Emitter;
        Assert.True(emitter.PresentationVisible);

        poses.Remove(Owner);
        sink.RefreshAttachedEmitters();

        Assert.Equal(1, system.ActiveEmitterCount);
        Assert.False(emitter.PresentationVisible);

        poses.Publish(Owner, Matrix4x4.CreateTranslation(8, 0, 0), Matrix4x4.Identity);
        sink.RefreshAttachedEmitters();
        Assert.True(emitter.PresentationVisible);
    }

    [Fact]
    public void PendingProjection_HidesPresentationWhileContinuingLiveAnchorRefresh()
    {
        const uint emitterId = 0x32000056u;
        var (system, sink, poses) = Harness(emitterId, totalDuration: 0.05f);
        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, logicalId: 12u));
        ParticleEmitter emitter = system.EnumerateLive().Single().Emitter;
        int emittedBeforePause = emitter.TotalEmitted;
        float spawnedAt = emitter.Particles[0].SpawnedAt;

        sink.SetEntityPresentationVisible(Owner, false);
        poses.Publish(
            Owner,
            Matrix4x4.CreateTranslation(12, 3, 0),
            Matrix4x4.Identity);
        sink.RefreshAttachedEmitters();
        system.Tick(1f);

        Assert.Equal(1, system.ActiveEmitterCount);
        Assert.Equal(new Vector3(12, 3, 0), emitter.AnchorPos);
        Assert.Equal(emittedBeforePause, emitter.TotalEmitted);
        Assert.Equal(spawnedAt, emitter.Particles[0].SpawnedAt);
        Assert.Equal(new Vector3(12, 3, 0), emitter.LastEmitOffset);
        Assert.False(emitter.SimulationEnabled);
        Assert.False(emitter.PresentationVisible);

        sink.SetEntityPresentationVisible(Owner, true);
        sink.RefreshAttachedEmitters();
        Assert.True(emitter.SimulationEnabled);
        Assert.True(emitter.PresentationVisible);
        Assert.Equal(spawnedAt, emitter.Particles[0].SpawnedAt);

        system.Tick(0.01f);
        Assert.Equal(1, system.ActiveEmitterCount);
        system.Tick(0.01f);
        Assert.Equal(0, system.ActiveEmitterCount);
    }

    [Fact]
    public void LogicalTeardown_RemovesEmitterThatWasSpatiallyPaused()
    {
        const uint emitterId = 0x32000057u;
        var (system, sink, _) = Harness(emitterId);
        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, logicalId: 13u));
        sink.SetEntityPresentationVisible(Owner, false);
        int deathNotices = 0;
        system.EmitterDied += _ => deathNotices++;

        sink.StopAllForEntity(Owner, fadeOut: false);

        Assert.Equal(0, system.ActiveEmitterCount);
        Assert.Equal(1, deathNotices);
    }

    [Fact]
    public void LogicalTeardown_AttemptsEveryEmitterWhenDeathSubscriberThrows()
    {
        const uint emitterId = 0x3200005Bu;
        var (system, sink, _) = Harness(emitterId);
        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, logicalId: 31u));
        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, logicalId: 32u));
        int deathAttempts = 0;
        system.EmitterDied += _ =>
        {
            deathAttempts++;
            throw new InvalidOperationException("observer failed after emitter removal");
        };

        AggregateException error = Assert.Throws<AggregateException>(
            () => sink.StopAllForEntity(Owner, fadeOut: false));

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(2, deathAttempts);
        Assert.Equal(0, system.ActiveEmitterCount);
        Assert.Equal(0, sink.ActiveBindingCount);
        Assert.Equal(0, sink.LogicalEmitterCount);
        Assert.Equal(0, sink.TrackedOwnerCount);

        sink.StopAllForEntity(Owner, fadeOut: false);
        Assert.Equal(2, deathAttempts);
    }

    [Fact]
    public void LogicalTeardown_DeathCallbackCannotResurrectSameOwnerEmitter()
    {
        const uint emitterId = 0x3200005Cu;
        var (system, sink, _) = Harness(emitterId);
        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, logicalId: 41u));
        int resurrectionAttempts = 0;
        system.EmitterDied += _ =>
        {
            resurrectionAttempts++;
            sink.OnHook(Owner, Vector3.Zero, Create(emitterId, logicalId: 42u));
        };

        sink.StopAllForEntity(Owner, fadeOut: false);

        Assert.Equal(1, resurrectionAttempts);
        Assert.Equal(0, system.ActiveEmitterCount);
        Assert.Equal(0, sink.ActiveBindingCount);
        Assert.Equal(0, sink.LogicalEmitterCount);
        Assert.Equal(0, sink.TrackedOwnerCount);
    }

    [Fact]
    public void ExaminationObjectPolicyAppliesToExistingAndFutureEmittersAndTearsDown()
    {
        const uint emitterId = 0x32000058u;
        var (system, sink, _) = Harness(emitterId);
        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, logicalId: 21u));

        sink.SetEntityExaminationObject(Owner, true);
        Assert.Equal(
            ParticleVisibilityPolicy.Examination,
            Assert.Single(system.EnumerateEmitters()).VisibilityPolicy);

        sink.OnHook(Owner, Vector3.Zero, Create(emitterId, logicalId: 22u));
        Assert.All(
            system.EnumerateEmitters(),
            emitter => Assert.Equal(
                ParticleVisibilityPolicy.Examination,
                emitter.VisibilityPolicy));
        Assert.Equal(1, sink.ExaminationOwnerCount);

        sink.StopAllForEntity(Owner, fadeOut: false);
        Assert.Equal(0, sink.ExaminationOwnerCount);
    }

    [Fact]
    public void PoseChangeWorkset_RefreshesOnlyChangedOwnersAndVisibilityEdges()
    {
        const uint emitterId = 0x32000059u;
        const int ownerCount = 2_000;
        var registry = new EmitterDescRegistry();
        registry.Register(MakeDesc(emitterId, attachLocal: true));
        var system = new ParticleSystem(registry, new Random(42));
        var poses = new MutablePoseSource();
        var sink = new ParticleHookSink(system, poses);

        for (uint owner = 1; owner <= ownerCount; owner++)
        {
            poses.Publish(owner, Matrix4x4.Identity, Matrix4x4.Identity);
            sink.OnHook(owner, Vector3.Zero, Create(emitterId, logicalId: 0));
        }

        sink.RefreshAttachedEmitters();
        Assert.Equal(0, sink.LastRefreshOwnerVisitCount);
        Assert.Equal(0, sink.LastRefreshBindingVisitCount);

        poses.Publish(777u, Matrix4x4.CreateTranslation(1, 2, 3), Matrix4x4.Identity);
        poses.Publish(777u, Matrix4x4.CreateTranslation(7, 8, 9), Matrix4x4.Identity);
        sink.RefreshAttachedEmitters();
        Assert.Equal(1, sink.LastRefreshOwnerVisitCount);
        Assert.Equal(1, sink.LastRefreshBindingVisitCount);
        Assert.Equal(new Vector3(7, 8, 9),
            system.EnumerateEmitters().Single(emitter => emitter.AttachedObjectId == 777u).AnchorPos);

        sink.RefreshAttachedEmitters();
        Assert.Equal(0, sink.LastRefreshOwnerVisitCount);

        sink.SetEntityPresentationVisible(777u, false);
        sink.SetEntityPresentationVisible(777u, true);
        sink.RefreshAttachedEmitters();
        Assert.Equal(1, sink.LastRefreshOwnerVisitCount);
        Assert.True(system.EnumerateEmitters()
            .Single(emitter => emitter.AttachedObjectId == 777u)
            .PresentationVisible);
    }

    [Fact]
    public void PoseChangeRaisedDuringRefresh_IsRetainedForNextRefresh()
    {
        const uint emitterId = 0x3200005Au;
        const uint firstOwner = 1u;
        const uint secondOwner = 2u;
        var registry = new EmitterDescRegistry();
        registry.Register(MakeDesc(emitterId, attachLocal: true));
        var system = new ParticleSystem(registry, new Random(42));
        var poses = new MutablePoseSource();
        poses.Publish(firstOwner, Matrix4x4.Identity, Matrix4x4.Identity);
        poses.Publish(secondOwner, Matrix4x4.Identity, Matrix4x4.Identity);
        var sink = new ParticleHookSink(system, poses);
        sink.OnHook(firstOwner, Vector3.Zero, Create(emitterId, logicalId: 0));
        sink.OnHook(secondOwner, Vector3.Zero, Create(emitterId, logicalId: 0));

        bool raised = false;
        poses.RootRead = owner =>
        {
            if (owner != firstOwner || raised)
                return;
            raised = true;
            poses.Publish(
                secondOwner,
                Matrix4x4.CreateTranslation(20, 0, 0),
                Matrix4x4.Identity);
        };
        poses.Publish(
            firstOwner,
            Matrix4x4.CreateTranslation(10, 0, 0),
            Matrix4x4.Identity);

        sink.RefreshAttachedEmitters();
        Assert.Equal(1, sink.LastRefreshOwnerVisitCount);
        sink.RefreshAttachedEmitters();
        Assert.Equal(1, sink.LastRefreshOwnerVisitCount);
        Assert.Equal(new Vector3(20, 0, 0),
            system.EnumerateEmitters()
                .Single(emitter => emitter.AttachedObjectId == secondOwner)
                .AnchorPos);
    }

    [Fact]
    public void EmitterKeepsItsOwnerCellAndStaysRenderableWithNoOwnerPresenceBesidesThePose()
    {
        const uint emitterId = 0x3200_0777u;
        const uint cell = 0x8A02015Eu;
        var registry = new EmitterDescRegistry();
        registry.Register(MakeDesc(emitterId, attachLocal: false, totalParticles: 0, totalDuration: 0f));
        var system = new ParticleSystem(registry, new Random(42));
        var poses = new CellPoseSource();
        poses.Publish(Owner, Matrix4x4.CreateTranslation(3, 4, 5), cell);
        var sink = new ParticleHookSink(system, poses);

        sink.OnHook(Owner, new Vector3(3, 4, 5), Create(emitterId, logicalId: 7u));
        ParticleEmitter emitter = system.EnumerateLive().Single().Emitter;
        Assert.Equal(cell, emitter.OwnerCellId);

        sink.RefreshAttachedEmitters();
        system.ApplyRetailView(new Vector3(3, 4, 5), new HashSet<uint> { cell }, hasCompletedView: true);
        Assert.True(emitter.PresentationVisible, "presentation visible");
        Assert.True(emitter.ViewEligible, "view eligible (owner cell in view, in range)");
        Assert.Equal(ParticleRenderPass.Scene, emitter.RenderPass);
        var byOwner = new List<ParticleEmitter>();
        system.CopyRenderableEmittersForOwners(
            ParticleRenderPass.Scene, new HashSet<uint> { Owner }, includeUnattached: false, byOwner);
        Assert.Single(byOwner);
        var inCell = new List<ParticleEmitter>();
        system.CopyRenderableEmittersInCell(ParticleRenderPass.Scene, cell, inCell);
        Assert.Single(inCell);
        Assert.Same(emitter, inCell[0]);

        sink.SetEntityPresentationVisible(Owner, false);
        system.CopyRenderableEmittersInCell(ParticleRenderPass.Scene, cell, inCell);
        Assert.Empty(inCell);
        sink.SetEntityPresentationVisible(Owner, true);
        sink.RefreshAttachedEmitters();
        system.ApplyRetailView(new Vector3(3, 4, 5), new HashSet<uint> { cell }, hasCompletedView: true);
        system.CopyRenderableEmittersInCell(ParticleRenderPass.Scene, cell, inCell);
        Assert.Single(inCell);
        Assert.Equal(cell, emitter.OwnerCellId);
    }

    private sealed class CellPoseSource :
        IEntityEffectPoseSource,
        IEntityEffectCellSource,
        IEntityEffectPoseChangeSource
    {
        private readonly Dictionary<uint, (Matrix4x4 Root, uint Cell)> _poses = new();
        public event Action<uint>? EffectPoseChanged;

        public void Publish(uint id, Matrix4x4 root, uint cellId)
        {
            _poses[id] = (root, cellId);
            EffectPoseChanged?.Invoke(id);
        }

        public bool TryGetRootPose(uint localEntityId, out Matrix4x4 rootWorld)
        {
            if (_poses.TryGetValue(localEntityId, out var pose))
            {
                rootWorld = pose.Root;
                return true;
            }
            rootWorld = default;
            return false;
        }

        public bool TryGetPartPose(uint localEntityId, int partIndex, out Matrix4x4 partLocal)
        {
            // One identity part at index 0, the shape the sink resolves an
            // emitter anchor through before it spawns.
            if (partIndex == 0 && _poses.ContainsKey(localEntityId))
            {
                partLocal = Matrix4x4.Identity;
                return true;
            }
            partLocal = default;
            return false;
        }

        public bool TryGetCellId(uint localEntityId, out uint cellId)
        {
            if (_poses.TryGetValue(localEntityId, out var pose))
            {
                cellId = pose.Cell;
                return true;
            }
            cellId = 0;
            return false;
        }
    }

    private sealed class MutablePoseSource :
        IEntityEffectPoseSource,
        IEntityEffectPoseChangeSource
    {
        private readonly Dictionary<uint, (Matrix4x4 Root, Matrix4x4[] Parts)> _poses = new();

        public event Action<uint>? EffectPoseChanged;
        public Action<uint>? RootRead { get; set; }

        public void Publish(uint id, Matrix4x4 root, params Matrix4x4[] parts)
        {
            _poses[id] = (root, parts);
            EffectPoseChanged?.Invoke(id);
        }

        public void Remove(uint id)
        {
            _poses.Remove(id);
            EffectPoseChanged?.Invoke(id);
        }

        public bool TryGetRootPose(uint localEntityId, out Matrix4x4 rootWorld)
        {
            RootRead?.Invoke(localEntityId);
            if (_poses.TryGetValue(localEntityId, out var pose))
            {
                rootWorld = pose.Root;
                return true;
            }
            rootWorld = default;
            return false;
        }

        public bool TryGetPartPose(uint localEntityId, int partIndex, out Matrix4x4 partLocal)
        {
            if (_poses.TryGetValue(localEntityId, out var pose)
                && partIndex >= 0
                && partIndex < pose.Parts.Length)
            {
                partLocal = pose.Parts[partIndex];
                return true;
            }
            partLocal = default;
            return false;
        }
    }
}
