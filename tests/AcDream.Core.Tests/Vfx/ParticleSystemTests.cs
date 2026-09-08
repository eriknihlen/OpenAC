using System.Linq;
using System.Numerics;
using AcDream.Core.Content;
using AcDream.Core.Vfx;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Vfx;

public sealed class ParticleSystemTests
{
    private static ParticleSystem MakeSystem()
        => new ParticleSystem(new EmitterDescRegistry(), new System.Random(42));

    private static EmitterDesc MakeDesc(ParticleType type = ParticleType.LocalVelocity,
        int maxParticles = 16, float emitRate = 20f, float lifetime = 1f)
    {
        return new EmitterDesc
        {
            DatId = 0x32000001u,
            Type = type,
            EmitRate = emitRate,
            MaxParticles = maxParticles,
            LifetimeMin = lifetime,
            LifetimeMax = lifetime,
            OffsetDir = Vector3.UnitZ,
            MinOffset = 0,
            MaxOffset = 0,
            SpawnDiskRadius = 0,
            InitialVelocity = new Vector3(0, 0, 1f),
            VelocityJitter = 0,
            StartSize = 0.5f,
            EndSize = 0.5f,
            StartAlpha = 1f,
            EndAlpha = 0f,
            Gravity = Vector3.Zero,
        };
    }

    private static EmitterDesc MakeInitialParticleDesc(
        ParticleType type,
        Vector3 a,
        Vector3 b,
        Vector3 c)
    {
        return new EmitterDesc
        {
            DatId = 0x3200AA01u,
            Type = type,
            MaxParticles = 1,
            InitialParticles = 1,
            LifetimeMin = 10f,
            LifetimeMax = 10f,
            Lifespan = 10f,
            LifespanRand = 0f,
            OffsetDir = Vector3.UnitZ,
            MinOffset = 0f,
            MaxOffset = 0f,
            InitialVelocity = Vector3.Zero,
            Gravity = Vector3.Zero,
            A = a,
            MinA = 1f,
            MaxA = 1f,
            B = b,
            MinB = 1f,
            MaxB = 1f,
            C = c,
            MinC = 1f,
            MaxC = 1f,
            StartSize = 0.5f,
            EndSize = 0.5f,
            StartAlpha = 1f,
            EndAlpha = 1f,
        };
    }

    [Fact]
    public void SpawnEmitter_ReturnsPositiveHandle_AndTracksEmitter()
    {
        var sys = MakeSystem();
        int h = sys.SpawnEmitter(
            MakeInitialParticleDesc(ParticleType.Still, Vector3.Zero, Vector3.Zero, Vector3.Zero),
            Vector3.Zero);
        Assert.True(h > 0);
        Assert.Equal(1, sys.ActiveEmitterCount);
        Assert.Equal(h, Assert.Single(sys.EnumerateLive().ToList()).Emitter.Handle);
    }

    [Fact]
    public void Tick_EmitsParticlesOverTime()
    {
        var sys = MakeSystem();
        // Lifetime=2s so none die in the 1s test window.
        sys.SpawnEmitter(MakeDesc(emitRate: 10f, maxParticles: 100, lifetime: 2f), Vector3.Zero);

        // 10/sec * 1s = ~10 particles.
        sys.Tick(0.5f);
        sys.Tick(0.5f);
        Assert.InRange(sys.ActiveParticleCount, 8, 12);
    }

    [Fact]
    public void Tick_ParticlesDieAtLifetime()
    {
        var sys = MakeSystem();
        int handle = sys.SpawnEmitter(MakeDesc(emitRate: 20f, lifetime: 0.5f, maxParticles: 100), Vector3.Zero);

        for (int i = 0; i < 20; i++) sys.Tick(0.05f);   // 1 second total
        int steadyState = sys.ActiveParticleCount;
        Assert.InRange(steadyState, 7, 13);

        // Now advance further with no new spawns; all should die.
        sys.StopEmitter(handle, fadeOut: true);
        for (int i = 0; i < 30; i++) sys.Tick(0.05f);   // 1.5s more than lifetime
        Assert.Equal(0, sys.ActiveParticleCount);
    }

    [Fact]
    public void LocalVelocity_IntegrationMovesParticles()
    {
        var sys = MakeSystem();
        var desc = MakeDesc(type: ParticleType.LocalVelocity);
        sys.SpawnEmitter(desc, Vector3.Zero);
        sys.Tick(0.1f);  // spawn a few
        sys.Tick(0.5f);  // move them 0.5s * 1 m/s = 0.5m in +Z

        var live = sys.EnumerateLive().ToList();
        Assert.NotEmpty(live);
        // First particle spawned ~0.1s ago has moved ~0.5s in +Z.
        // Just assert z-positions are spread (not all at origin).
        bool anyMoved = live.Any(p => p.Emitter.Particles[p.Index].Position.Z > 0.3f);
        Assert.True(anyMoved, "Expected at least one particle to have moved in +Z");
    }

    [Fact]
    public void Parabolic_GravityApplied()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000002u,
            Type = ParticleType.ParabolicLVGA,
            EmitRate = 10f,
            MaxParticles = 100,
            LifetimeMin = 2f,
            LifetimeMax = 2f,
            OffsetDir = Vector3.UnitZ,
            InitialVelocity = new Vector3(0, 0, 5f),   // straight up
            StartSize = 0.5f,
            EndSize = 0.5f,
            StartAlpha = 1f,
            EndAlpha = 0f,
            Gravity = new Vector3(0, 0, -10f),         // strong gravity
        };
        sys.SpawnEmitter(desc, Vector3.Zero);

        // Spawn burst.
        sys.Tick(0.1f);
        sys.Tick(0.5f);   // 0.5s after spawn: v_z = 5 - 10*0.5 = 0; z peaks

        // Keep integrating; gravity should pull particles back below z=0
        // by t ~= 1.0s total flight.
        for (int i = 0; i < 20; i++) sys.Tick(0.1f);

        var anyBelow = sys.EnumerateLive().Any(p => p.Emitter.Particles[p.Index].Position.Z < 0f);
        Assert.True(anyBelow || sys.ActiveParticleCount == 0,
            "Expected some parabolic particles to fall below 0");
    }

    [Fact]
    public void Explode_MovesOutwardFromAnchor()
    {
        var sys = MakeSystem();
        // Seed particles with small offsets in spawn disk so they have
        // non-zero radial distance from anchor.
        var desc = new EmitterDesc
        {
            DatId = 0x32000003u,
            Type = ParticleType.Explode,
            EmitRate = 20f,
            MaxParticles = 100,
            LifetimeMin = 2f,
            LifetimeMax = 2f,
            OffsetDir = Vector3.UnitZ,
            MinOffset = 0.5f,
            MaxOffset = 0.5f,
            SpawnDiskRadius = 0.5f,
            InitialVelocity = new Vector3(1, 0, 0),  // magnitude = 1
            StartSize = 0.5f,
            EndSize = 0.5f,
            StartAlpha = 1f,
            EndAlpha = 0f,
        };
        sys.SpawnEmitter(desc, Vector3.Zero);

        sys.Tick(0.1f);
        sys.Tick(0.5f);

        // All alive particles should be further from origin than their
        // initial disk radius (~0.5) because Explode pushes outward at
        // speed 1 m/s.
        var live = sys.EnumerateLive().ToList();
        Assert.NotEmpty(live);
        foreach (var (em, idx) in live)
            Assert.True(em.Particles[idx].Position.Length() > 0.3f);
    }

    [Fact]
    public void StopEmitter_KillsAllParticles()
    {
        var sys = MakeSystem();
        int h = sys.SpawnEmitter(MakeDesc(emitRate: 10f, maxParticles: 20), Vector3.Zero);
        sys.Tick(0.5f);
        Assert.True(sys.ActiveParticleCount > 0);

        sys.StopEmitter(h, fadeOut: false);
        sys.Tick(0.01f);

        Assert.Equal(0, sys.ActiveParticleCount);
    }

    [Fact]
    public void StopEmitter_FadeOut_PreservesCurrentParticles()
    {
        var sys = MakeSystem();
        int h = sys.SpawnEmitter(MakeDesc(emitRate: 10f, lifetime: 1f, maxParticles: 20), Vector3.Zero);
        sys.Tick(0.3f);
        int before = sys.ActiveParticleCount;
        Assert.True(before > 0);

        sys.StopEmitter(h, fadeOut: true);
        sys.Tick(0.1f);  // particles still alive, no NEW spawns
        int after = sys.ActiveParticleCount;
        Assert.Equal(before, after);
    }

    [Fact]
    public void MaxParticles_CapEnforced()
    {
        var sys = MakeSystem();
        // Low cap, high rate, long life → rapidly hit cap.
        sys.SpawnEmitter(MakeDesc(emitRate: 100f, lifetime: 10f, maxParticles: 5), Vector3.Zero);

        sys.Tick(1f);   // would spawn 100 if unbounded; cap at 5.
        Assert.InRange(sys.ActiveParticleCount, 1, 5);
    }

    [Fact]
    public void EmitterDescRegistry_RejectsUnknownIdWithoutInventingFallback()
    {
        var reg = new EmitterDescRegistry();
        Assert.False(reg.TryGet(0xDEADBEEFu, out _));
        Assert.Throws<KeyNotFoundException>(() => reg.Get(0xDEADBEEFu));
    }

    [Fact]
    public void EmitterDescRegistry_Register_StoresById()
    {
        var reg = new EmitterDescRegistry();
        var desc = new EmitterDesc { DatId = 0x32001234u, Type = ParticleType.Still };
        reg.Register(desc);
        Assert.Same(desc, reg.Get(0x32001234u));
    }

    [Fact]
    public void EmitterDescRegistry_NegativeCachesMissingDatAndRegisterClearsFailure()
    {
        int calls = 0;
        var reg = new EmitterDescRegistry(_ =>
        {
            calls++;
            return null;
        });
        const uint emitterId = 0x3200DEADu;

        Assert.False(reg.TryGet(emitterId, out _, out var first));
        Assert.False(reg.TryGet(emitterId, out _, out var second));

        Assert.Equal(1, calls);
        Assert.Equal(
            EmitterDescResolutionFailureKind.MissingEmitterInfo,
            first.Kind);
        Assert.Equal(first, second);
        Assert.Equal(1, reg.FailureCount);

        var desc = new EmitterDesc { DatId = emitterId };
        reg.Register(desc);
        Assert.True(reg.TryGet(emitterId, out var resolved, out var failure));
        Assert.Same(desc, resolved);
        Assert.Equal(EmitterDescResolutionFailureKind.None, failure.Kind);
        Assert.Equal(0, reg.FailureCount);
    }

    [Fact]
    public void LocalVelocity_TransformsABySpawnRotation()
    {
        var sys = MakeSystem();
        var desc = MakeInitialParticleDesc(
            ParticleType.LocalVelocity,
            Vector3.UnitX,
            Vector3.Zero,
            Vector3.Zero);

        sys.SpawnEmitter(desc, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f));
        sys.Tick(1f);

        var live = sys.EnumerateLive().Single();
        var pos = live.Emitter.Particles[live.Index].Position;
        Assert.InRange(pos.X, -0.0001f, 0.0001f);
        Assert.InRange(pos.Y, 0.9999f, 1.0001f);
    }

    [Fact]
    public void GlobalVelocity_DoesNotTransformABySpawnRotation()
    {
        var sys = MakeSystem();
        var desc = MakeInitialParticleDesc(
            ParticleType.GlobalVelocity,
            Vector3.UnitX,
            Vector3.Zero,
            Vector3.Zero);

        sys.SpawnEmitter(desc, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f));
        sys.Tick(1f);

        var live = sys.EnumerateLive().Single();
        var pos = live.Emitter.Particles[live.Index].Position;
        Assert.InRange(pos.X, 0.9999f, 1.0001f);
        Assert.InRange(pos.Y, -0.0001f, 0.0001f);
    }

    [Fact]
    public void ParabolicLVLA_TransformsLocalAcceleration()
    {
        var sys = MakeSystem();
        var desc = MakeInitialParticleDesc(
            ParticleType.ParabolicLVLA,
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.Zero);

        sys.SpawnEmitter(desc, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f));
        sys.Tick(1f);

        var live = sys.EnumerateLive().Single();
        var pos = live.Emitter.Particles[live.Index].Position;
        Assert.InRange(pos.X, -0.0001f, 0.0001f);
        Assert.InRange(pos.Y, 0.4999f, 0.5001f);
    }

    [Fact]
    public void ParabolicLVGA_KeepsGlobalAcceleration()
    {
        var sys = MakeSystem();
        var desc = MakeInitialParticleDesc(
            ParticleType.ParabolicLVGA,
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.Zero);

        sys.SpawnEmitter(desc, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f));
        sys.Tick(1f);

        var live = sys.EnumerateLive().Single();
        var pos = live.Emitter.Particles[live.Index].Position;
        Assert.InRange(pos.X, 0.4999f, 0.5001f);
        Assert.InRange(pos.Y, -0.0001f, 0.0001f);
    }

    [Fact]
    public void EmitterDescRegistry_FromDat_PreservesRetailEnumValuesAndRates()
    {
        var dat = new DatReaderWriter.DBObjs.ParticleEmitter
        {
            EmitterType = DatReaderWriter.Enums.EmitterType.BirthratePerSec,
            ParticleType = DatReaderWriter.Enums.ParticleType.Swarm,
            GfxObjId = 0x01000001u,
            HwGfxObjId = 0x01000002u,
            Birthrate = 0.25,
            MaxParticles = 17,
            InitialParticles = 3,
            TotalParticles = 9,
            TotalSeconds = 4,
            Lifespan = 2,
            LifespanRand = 0.5,
            A = new Vector3(1, 0, 0),
            MinA = 0.5f,
            MaxA = 2f,
            StartScale = 0.2f,
            FinalScale = 0.8f,
            StartTrans = 1f,
            FinalTrans = 0f,
            IsParentLocal = true,
        };

        var desc = EmitterDescRegistry.FromDat(0x32000099u, dat);

        Assert.Equal(ParticleType.Swarm, desc.Type);
        Assert.Equal(ParticleEmitterKind.BirthratePerSec, desc.EmitterKind);
        Assert.Equal(4f, desc.EmitRate);
        Assert.Equal(0x01000001u, desc.GfxObjId);
        Assert.Equal(0x01000002u, desc.HwGfxObjId);
        Assert.Equal(3, desc.InitialParticles);
        Assert.Equal(9, desc.TotalParticles);
        Assert.Equal(1.5f, desc.LifetimeMin);
        Assert.Equal(2.5f, desc.LifetimeMax);
        Assert.Equal(0f, desc.StartAlpha);
        Assert.Equal(1f, desc.EndAlpha);
        Assert.Equal(EmitterFlags.Billboard | EmitterFlags.FaceCamera | EmitterFlags.AttachLocal, desc.Flags);
        Assert.True((desc.Flags & EmitterFlags.AttachLocal) != 0);
    }

    [Fact]
    public void EmitterDescRegistry_DoesNotSubstituteSoftwareGfxForMissingHardwareGfx()
    {
        var dat = new DatReaderWriter.DBObjs.ParticleEmitter
        {
            GfxObjId = 0x01000001u,
            HwGfxObjId = 0u,
        };

        Assert.Equal(0u, EmitterDescRegistry.GetRetailHardwareGfxObjId(dat));
    }

    [Fact]
    public void EmitterDescRegistry_ClassifiesAndCachesAuthoredHardwarelessEmitter()
    {
        const uint emitterId = 0x320002D6u;
        var dats = new FakeDatObjectSource();
        dats.Add(
            emitterId,
            new DatReaderWriter.DBObjs.ParticleEmitter
            {
                GfxObjId = 0x010016C9u,
                HwGfxObjId = 0u,
            });
        var reg = new EmitterDescRegistry(dats);

        Assert.False(reg.TryGet(emitterId, out _, out var first));
        Assert.False(reg.TryGet(emitterId, out _, out var second));

        Assert.Equal(
            EmitterDescResolutionFailureKind.InvalidHardwareGfxObjId,
            first.Kind);
        Assert.Equal(first, second);
        Assert.Equal(1, dats.GetCallCount(emitterId));
        Assert.Equal(1, reg.FailureCount);
    }

    [Fact]
    public void RetailParticleDegradeDistance_MatchesRetailEntrySelection()
    {
        var one = new List<GfxObjInfo>
        {
            new() { MaxDist = 11f },
        };
        var two = new List<GfxObjInfo>
        {
            new() { MaxDist = 21f },
            new() { MaxDist = 22f },
        };
        var four = new List<GfxObjInfo>
        {
            new() { MaxDist = 31f },
            new() { MaxDist = 32f },
            new() { MaxDist = 33f },
            new() { MaxDist = 34f },
        };

        Assert.Equal(100f, RetailParticleDegradeDistance.FromEntries(null));
        Assert.Equal(11f, RetailParticleDegradeDistance.FromEntries(one));
        Assert.Equal(21f, RetailParticleDegradeDistance.FromEntries(two));
        Assert.Equal(33f, RetailParticleDegradeDistance.FromEntries(four));
    }

    [Fact]
    public void RetailParticleDegradeDistance_PreservesRawAuthoredValue()
    {
        Assert.Equal(-1f, RetailParticleDegradeDistance.FromEntries(
            new List<GfxObjInfo> { new() { MaxDist = -1f } }));
        Assert.Equal(float.PositiveInfinity, RetailParticleDegradeDistance.FromEntries(
            new List<GfxObjInfo> { new() { MaxDist = float.PositiveInfinity } }));
        Assert.True(float.IsNaN(RetailParticleDegradeDistance.FromEntries(
            new List<GfxObjInfo> { new() { MaxDist = float.NaN } })));
    }

    [Fact]
    public void ApplyRetailView_UsesOwnerVisibilityAndInclusiveAuthoredDistance()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000076u,
            Type = ParticleType.Still,
            MaxDegradeDistance = 10f,
            MaxParticles = 1,
            InitialParticles = 1,
            LifetimeMin = 10f,
            LifetimeMax = 10f,
            StartSize = 1f,
            EndSize = 1f,
            StartAlpha = 1f,
            EndAlpha = 1f,
        };
        int handle = sys.SpawnEmitter(
            desc,
            new Vector3(10f, 0f, 0f),
            attachedObjectId: 7u);
        sys.UpdateEmitterOwnerCell(handle, 0x01010001u);

        sys.ApplyRetailView(Vector3.Zero, new HashSet<uint> { 0x01010001u }, true);
        Assert.True(Assert.Single(sys.EnumerateEmitters()).ViewEligible);

        sys.ApplyRetailView(Vector3.Zero, new HashSet<uint>(), true);
        Assert.False(Assert.Single(sys.EnumerateEmitters()).ViewEligible);

        sys.ApplyRetailView(
            Vector3.Zero,
            new HashSet<uint> { 0x01010001u },
            true,
            rangeMultiplier: 0.5f);
        Assert.False(Assert.Single(sys.EnumerateEmitters()).ViewEligible);
    }

    [Fact]
    public void ApplyRetailView_UsesLandscapeMembershipButEnvCellConstantVirtual()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x3200007Bu,
            Type = ParticleType.Still,
            MaxDegradeDistance = 10f,
            MaxParticles = 1,
        };
        int outdoor = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: 21u);
        int environment = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: 22u);
        int cellLess = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: 23u);
        sys.UpdateEmitterOwnerCell(outdoor, 0x0101_0001u);
        sys.UpdateEmitterOwnerCell(environment, 0x0101_0100u);

        sys.ApplyRetailView(Vector3.Zero, new HashSet<uint> { 0x0101_0001u }, true);

        ParticleEmitter[] first = sys.EnumerateEmitters().ToArray();
        Assert.True(first.Single(emitter => emitter.Handle == outdoor).ViewEligible);
        Assert.True(first.Single(emitter => emitter.Handle == environment).ViewEligible);
        Assert.False(first.Single(emitter => emitter.Handle == cellLess).ViewEligible);

        sys.ApplyRetailView(Vector3.Zero, new HashSet<uint>(), true);

        ParticleEmitter[] second = sys.EnumerateEmitters().ToArray();
        Assert.False(second.Single(emitter => emitter.Handle == outdoor).ViewEligible);
        Assert.True(second.Single(emitter => emitter.Handle == environment).ViewEligible);
        Assert.False(second.Single(emitter => emitter.Handle == cellLess).ViewEligible);
    }

    [Fact]
    public void ApplyRetailView_NoCompletedViewRejectsBothCellFamilies()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x3200007Du,
            Type = ParticleType.Still,
            MaxDegradeDistance = 10f,
            MaxParticles = 1,
        };
        int outdoor = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: 31u);
        int environment = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: 32u);
        sys.UpdateEmitterOwnerCell(outdoor, 0x0101_0001u);
        sys.UpdateEmitterOwnerCell(environment, 0x0101_0100u);

        sys.ApplyRetailView(
            Vector3.Zero,
            new HashSet<uint> { 0x0101_0001u },
            hasCompletedView: false);

        Assert.All(sys.EnumerateEmitters(), emitter => Assert.False(emitter.ViewEligible));
    }

    [Fact]
    public void ApplyRetailView_UnorderedOwnerDistanceMatchesRetailX87Comparison()
    {
        var sys = MakeSystem();
        int handle = sys.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000079u,
                Type = ParticleType.Still,
                MaxDegradeDistance = 100f,
                MaxParticles = 1,
            },
            Vector3.Zero,
            attachedObjectId: 12u);
        sys.UpdateEmitterOwnerPosition(handle, new Vector3(float.NaN, 0f, 0f));
        sys.UpdateEmitterOwnerCell(handle, 0x0101000Cu);

        sys.ApplyRetailView(
            Vector3.Zero,
            new HashSet<uint> { 0x0101000Cu },
            true);

        Assert.True(Assert.Single(sys.EnumerateEmitters()).ViewEligible);
    }

    [Fact]
    public void ApplyRetailView_UsesOverflowSafeDirectDistanceAndRawRange()
    {
        var visible = new HashSet<uint> { 0x0101000Du };

        static ParticleSystem Spawn(float maxDistance, Vector3 owner, out int handle)
        {
            var system = MakeSystem();
            handle = system.SpawnEmitter(
                new EmitterDesc
                {
                    DatId = 0x3200007Au,
                    Type = ParticleType.Still,
                    MaxDegradeDistance = maxDistance,
                    MaxParticles = 1,
                },
                owner,
                attachedObjectId: 13u);
            system.UpdateEmitterOwnerPosition(handle, owner);
            system.UpdateEmitterOwnerCell(handle, 0x0101000Du);
            return system;
        }

        ParticleSystem huge = Spawn(float.MaxValue, new Vector3(1e20f, 0f, 0f), out _);
        huge.ApplyRetailView(Vector3.Zero, visible, true);
        Assert.True(Assert.Single(huge.EnumerateEmitters()).ViewEligible);

        ParticleSystem negative = Spawn(-1f, Vector3.Zero, out _);
        negative.ApplyRetailView(Vector3.Zero, visible, true);
        Assert.False(Assert.Single(negative.EnumerateEmitters()).ViewEligible);

        ParticleSystem zero = Spawn(0f, Vector3.Zero, out _);
        zero.ApplyRetailView(Vector3.Zero, visible, true);
        Assert.True(Assert.Single(zero.EnumerateEmitters()).ViewEligible);

        ParticleSystem nan = Spawn(float.NaN, new Vector3(100f, 0f, 0f), out _);
        nan.ApplyRetailView(Vector3.Zero, visible, true);
        Assert.True(Assert.Single(nan.EnumerateEmitters()).ViewEligible);

        ParticleSystem infinity = Spawn(float.PositiveInfinity, new Vector3(float.MaxValue, 0f, 0f), out _);
        infinity.ApplyRetailView(Vector3.Zero, visible, true);
        Assert.True(Assert.Single(infinity.EnumerateEmitters()).ViewEligible);
    }

    [Fact]
    public void InfiniteEmitter_DegradedTimeFreezesParticleAgeWithoutCatchUp()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000077u,
            Type = ParticleType.Still,
            MaxDegradeDistance = 5f,
            MaxParticles = 1,
            InitialParticles = 1,
            TotalParticles = 0,
            TotalDuration = 0f,
            LifetimeMin = 10f,
            LifetimeMax = 10f,
            StartSize = 1f,
            EndSize = 1f,
            StartAlpha = 1f,
            EndAlpha = 1f,
        };
        int handle = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: 9u);
        sys.UpdateEmitterOwnerCell(handle, 0x01010009u);
        var visible = new HashSet<uint> { 0x01010009u };

        sys.ApplyRetailView(Vector3.Zero, visible, true);
        sys.Tick(1f);
        Assert.Equal(1f, Assert.Single(sys.EnumerateLive()).Emitter.Particles[0].Age, 4);

        sys.ApplyRetailView(new Vector3(6f, 0f, 0f), visible, true);
        sys.Tick(5f);
        Assert.True(Assert.Single(sys.EnumerateEmitters()).DegradedOut);

        sys.ApplyRetailView(Vector3.Zero, visible, true);
        sys.Tick(1f);
        ParticleEmitter emitter = Assert.Single(sys.EnumerateEmitters());
        Assert.False(emitter.DegradedOut);
        Assert.Equal(2f, emitter.Particles[0].Age, 4);
    }

    [Fact]
    public void ApplyRetailView_ExaminationAndPassOwnedEmittersBypassWorldGate()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x3200007Cu,
            Type = ParticleType.Still,
            MaxDegradeDistance = 1f,
            MaxParticles = 1,
        };
        sys.SpawnEmitter(
            desc,
            new Vector3(100f, 0f, 0f),
            visibilityPolicy: ParticleVisibilityPolicy.Examination);
        sys.SpawnEmitter(
            desc,
            new Vector3(100f, 0f, 0f),
            renderPass: ParticleRenderPass.SkyPreScene,
            visibilityPolicy: ParticleVisibilityPolicy.PassOwned);

        sys.ApplyRetailView(Vector3.Zero, new HashSet<uint>(), false);

        Assert.All(sys.EnumerateEmitters(), emitter => Assert.True(emitter.ViewEligible));
    }

    [Fact]
    public void DenseSuspendedSet_IsExcludedFromSimulationViewAndRenderWorksets()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000080u,
            Type = ParticleType.Still,
            MaxDegradeDistance = 100f,
            MaxParticles = 1,
            InitialParticles = 1,
            LifetimeMin = 100f,
            LifetimeMax = 100f,
        };
        var visibleCells = new HashSet<uint> { 0x01010001u };

        for (int i = 0; i < 2_000; i++)
        {
            int handle = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: (uint)(i + 1));
            sys.UpdateEmitterOwnerCell(handle, 0x01010001u);
            if (i >= 3)
            {
                sys.SetEmitterPresentationVisible(handle, false);
                sys.SetEmitterSimulationEnabled(handle, false);
            }
        }

        sys.ApplyRetailView(Vector3.Zero, visibleCells, hasCompletedView: true);
        sys.Tick(0.01f);

        Assert.Equal(3, sys.LastRetailViewEmitterVisitCount);
        Assert.Equal(3, sys.LastTickEmitterVisitCount);
        Assert.Equal(3, sys.EnumerateRenderableEmitters(ParticleRenderPass.Scene).Count());
        Assert.Equal(2_000, sys.ActiveEmitterCount);
    }

    [Fact]
    public void OwnerRenderScope_VisitsOnlyRequestedOwnersAndUnattachedEmittersInSpawnOrder()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000083u,
            Type = ParticleType.Still,
            MaxParticles = 1,
        };
        int unattached = sys.SpawnEmitter(desc, Vector3.Zero);
        int ownerSeven = 0;
        int ownerNine = 0;
        for (uint owner = 1; owner <= 2_000; owner++)
        {
            int handle = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: owner);
            if (owner == 7u)
                ownerSeven = handle;
            if (owner == 9u)
                ownerNine = handle;
        }

        var destination = new List<ParticleEmitter>();
        sys.CopyRenderableEmittersForOwners(
            ParticleRenderPass.Scene,
            new HashSet<uint> { 9u, 7u, 4_000u },
            includeUnattached: true,
            destination);

        Assert.Equal(3, sys.LastRenderScopeEmitterVisitCount);
        Assert.Equal(new[] { unattached, ownerSeven, ownerNine },
            destination.Select(emitter => emitter.Handle));

        sys.CopyRenderableEmittersForOwners(
            ParticleRenderPass.Scene,
            new HashSet<uint> { 7u, 9u },
            includeUnattached: false,
            destination,
            excludedAttachedOwnerIds: new HashSet<uint> { 7u });
        Assert.Equal(1, sys.LastRenderScopeEmitterVisitCount);
        Assert.Equal(new[] { ownerNine }, destination.Select(emitter => emitter.Handle));
    }

    [Fact]
    public void UnattachedCellScope_SplitsEmittersByOwnerCellKind()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000083u,
            Type = ParticleType.Still,
            MaxParticles = 1,
        };
        int outdoor = sys.SpawnEmitter(desc, Vector3.Zero);
        sys.UpdateEmitterOwnerCell(outdoor, 0xA9B40021u);
        int interior = sys.SpawnEmitter(desc, Vector3.Zero);
        sys.UpdateEmitterOwnerCell(interior, 0xA9B40100u);
        int cellLess = sys.SpawnEmitter(desc, Vector3.Zero);

        var destination = new List<ParticleEmitter>();
        var none = new HashSet<uint>();

        sys.CopyRenderableEmittersForOwners(
            ParticleRenderPass.Scene,
            none,
            includeUnattached: true,
            destination,
            unattachedCellScope: UnattachedEmitterCellScope.OutdoorCells);
        Assert.Equal(new[] { outdoor }, destination.Select(e => e.Handle));

        sys.CopyRenderableEmittersForOwners(
            ParticleRenderPass.Scene,
            none,
            includeUnattached: true,
            destination,
            unattachedCellScope: UnattachedEmitterCellScope.InteriorCells);
        Assert.Equal(new[] { interior }, destination.Select(e => e.Handle));

        sys.CopyRenderableEmittersForOwners(
            ParticleRenderPass.Scene,
            none,
            includeUnattached: true,
            destination,
            unattachedCellScope: UnattachedEmitterCellScope.Any);
        Assert.Equal(
            new[] { outdoor, interior, cellLess },
            destination.Select(e => e.Handle));
    }

    [Fact]
    public void SpatialReentryWaitsForFreshRetailViewBeforeBecomingRenderable()
    {
        var sys = MakeSystem();
        int handle = sys.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000084u,
                Type = ParticleType.Still,
                MaxDegradeDistance = 100f,
                MaxParticles = 1,
            },
            Vector3.Zero,
            attachedObjectId: 84u);
        sys.UpdateEmitterOwnerCell(handle, 0x01010001u);
        var visible = new HashSet<uint> { 0x01010001u };
        var destination = new List<ParticleEmitter>();

        sys.ApplyRetailView(Vector3.Zero, visible, hasCompletedView: true);
        Assert.Single(sys.EnumerateRenderableEmitters(ParticleRenderPass.Scene));

        sys.SetEmitterPresentationVisible(handle, false);
        sys.SetEmitterSimulationEnabled(handle, false);
        Assert.False(Assert.Single(sys.EnumerateEmitters()).ViewEligible);

        sys.SetEmitterPresentationVisible(handle, true);
        sys.SetEmitterSimulationEnabled(handle, true);
        sys.CopyRenderableEmittersForOwners(
            ParticleRenderPass.Scene,
            new HashSet<uint> { 84u },
            includeUnattached: false,
            destination);
        Assert.Empty(destination);

        sys.ApplyRetailView(Vector3.Zero, visible, hasCompletedView: true);
        sys.CopyRenderableEmittersForOwners(
            ParticleRenderPass.Scene,
            new HashSet<uint> { 84u },
            includeUnattached: false,
            destination);
        Assert.Single(destination);
    }


    [Fact]
    public void CellIndex_AddRemoveMove_TracksRenderableEmittersPerCell()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000090u,
            Type = ParticleType.Still,
            MaxParticles = 1,
        };
        int handle = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: 501u);
        sys.UpdateEmitterOwnerCell(handle, 0x0102_0001u);
        var visible = new HashSet<uint> { 0x0102_0001u, 0x0102_0002u };
        sys.ApplyRetailView(Vector3.Zero, visible, hasCompletedView: true);

        var destination = new List<ParticleEmitter>();

        sys.CopyRenderableEmittersInCell(ParticleRenderPass.Scene, 0x0102_0001u, destination);
        Assert.Equal(new[] { handle }, destination.Select(e => e.Handle));

        sys.CopyRenderableEmittersInCell(ParticleRenderPass.Scene, 0x0102_0002u, destination);
        Assert.Empty(destination);

        sys.UpdateEmitterOwnerCell(handle, 0x0102_0002u);
        sys.CopyRenderableEmittersInCell(ParticleRenderPass.Scene, 0x0102_0001u, destination);
        Assert.Empty(destination);
        sys.CopyRenderableEmittersInCell(ParticleRenderPass.Scene, 0x0102_0002u, destination);
        Assert.Equal(new[] { handle }, destination.Select(e => e.Handle));

        sys.StopEmitter(handle, fadeOut: false);
        sys.CopyRenderableEmittersInCell(ParticleRenderPass.Scene, 0x0102_0002u, destination);
        Assert.Empty(destination);
    }


    [Fact]
    public void HasRenderableEmittersInCell_AddRemoveMove_TracksTheSameLifecycleAsCopy()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000092u,
            Type = ParticleType.Still,
            MaxParticles = 1,
        };

        Assert.False(sys.HasRenderableEmittersInCell(ParticleRenderPass.Scene, 0x0102_0001u));

        int handle = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: 503u);
        sys.UpdateEmitterOwnerCell(handle, 0x0102_0001u);
        var visible = new HashSet<uint> { 0x0102_0001u, 0x0102_0002u };
        sys.ApplyRetailView(Vector3.Zero, visible, hasCompletedView: true);

        Assert.True(sys.HasRenderableEmittersInCell(ParticleRenderPass.Scene, 0x0102_0001u));
        Assert.False(sys.HasRenderableEmittersInCell(ParticleRenderPass.Scene, 0x0102_0002u));

        sys.UpdateEmitterOwnerCell(handle, 0x0102_0002u);
        Assert.False(sys.HasRenderableEmittersInCell(ParticleRenderPass.Scene, 0x0102_0001u));
        Assert.True(sys.HasRenderableEmittersInCell(ParticleRenderPass.Scene, 0x0102_0002u));

        sys.StopEmitter(handle, fadeOut: false);
        Assert.False(sys.HasRenderableEmittersInCell(ParticleRenderPass.Scene, 0x0102_0002u));
    }

    [Fact]
    public void CellIndex_MultipleEmittersInOneCell_EnumerateInSpawnOrder()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000091u,
            Type = ParticleType.Still,
            MaxParticles = 1,
        };
        int first = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: 601u);
        sys.UpdateEmitterOwnerCell(first, 0x0203_0005u);
        int second = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: 602u);
        sys.UpdateEmitterOwnerCell(second, 0x0203_0005u);
        int unattachedInSameCell = sys.SpawnEmitter(desc, Vector3.Zero);
        sys.UpdateEmitterOwnerCell(unattachedInSameCell, 0x0203_0005u);

        var visible = new HashSet<uint> { 0x0203_0005u };
        sys.ApplyRetailView(Vector3.Zero, visible, hasCompletedView: true);

        var destination = new List<ParticleEmitter>();
        sys.CopyRenderableEmittersInCell(ParticleRenderPass.Scene, 0x0203_0005u, destination);

        Assert.Equal(
            new[] { first, second, unattachedInSameCell },
            destination.Select(e => e.Handle));
    }

    [Fact]
    public void CopyRenderableEmittersInCell_FindsAnEmitterWhoseOwnerHasNoOtherPresence()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000092u,
            Type = ParticleType.Still,
            MaxParticles = 1,
        };
        // A large, otherwise-unused owner id stands in for a hidden/suspended
        // entity that publishes no registry rows anywhere; ParticleSystem
        // never looks at any such registry, so nothing needs to simulate one.
        const uint hiddenOwnerId = 0x5000_00FFu;
        const uint arrivalCellId = 0x8A02_0141u;
        int handle = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: hiddenOwnerId);
        sys.UpdateEmitterOwnerCell(handle, arrivalCellId);

        var visible = new HashSet<uint> { arrivalCellId };
        sys.ApplyRetailView(Vector3.Zero, visible, hasCompletedView: true);

        var destination = new List<ParticleEmitter>();
        sys.CopyRenderableEmittersInCell(ParticleRenderPass.Scene, arrivalCellId, destination);

        Assert.Equal(new[] { handle }, destination.Select(e => e.Handle));
    }

    [Fact]
    public void OrderedIndexes_PreserveSpawnOrderAcrossHardRemovalAndPassFiltering()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000081u,
            Type = ParticleType.Still,
            MaxParticles = 1,
        };
        int first = sys.SpawnEmitter(desc, Vector3.Zero);
        int removedScene = sys.SpawnEmitter(desc, Vector3.Zero);
        int sky = sys.SpawnEmitter(
            desc,
            Vector3.Zero,
            renderPass: ParticleRenderPass.SkyPreScene,
            visibilityPolicy: ParticleVisibilityPolicy.PassOwned);
        int removedSky = sys.SpawnEmitter(
            desc,
            Vector3.Zero,
            renderPass: ParticleRenderPass.SkyPreScene,
            visibilityPolicy: ParticleVisibilityPolicy.PassOwned);
        int last = sys.SpawnEmitter(desc, Vector3.Zero);

        sys.StopEmitter(removedScene, fadeOut: false);
        sys.StopEmitter(removedSky, fadeOut: false);

        Assert.Equal(new[] { first, sky, last },
            sys.EnumerateEmitters().Select(emitter => emitter.Handle));
        Assert.Equal(new[] { first, last },
            sys.EnumerateRenderableEmitters(ParticleRenderPass.Scene)
                .Select(emitter => emitter.Handle));
        Assert.Equal(new[] { sky },
            sys.EnumerateRenderableEmitters(ParticleRenderPass.SkyPreScene)
                .Select(emitter => emitter.Handle));
    }

    [Fact]
    public void TickSnapshot_ToleratesNestedEmitterRemovalFromDeathCallback()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000082u,
            Type = ParticleType.Still,
            MaxParticles = 1,
            InitialParticles = 1,
            TotalParticles = 1,
            LifetimeMin = 0.01f,
            LifetimeMax = 0.01f,
            Lifespan = 0.01f,
        };
        int first = sys.SpawnEmitter(desc, Vector3.Zero);
        int second = sys.SpawnEmitter(desc, Vector3.Zero);
        sys.EmitterDied += handle =>
        {
            if (handle == first)
                sys.StopEmitter(second, fadeOut: false);
        };

        sys.Tick(0.02f);
        sys.Tick(0.01f);

        Assert.Equal(1, sys.LastTickEmitterVisitCount);
        Assert.Equal(0, sys.ActiveEmitterCount);
        Assert.Empty(sys.EnumerateEmitters());
    }

    [Fact]
    public void FiniteEmitter_DegradedBranchExpiresAndRemovesIt()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32000078u,
            Type = ParticleType.Still,
            MaxDegradeDistance = 1f,
            MaxParticles = 1,
            InitialParticles = 1,
            TotalParticles = 1,
            LifetimeMin = 0.5f,
            LifetimeMax = 0.5f,
            StartSize = 1f,
            EndSize = 1f,
            StartAlpha = 1f,
            EndAlpha = 1f,
        };
        int handle = sys.SpawnEmitter(desc, Vector3.Zero, attachedObjectId: 10u);
        sys.UpdateEmitterOwnerCell(handle, 0x0101000Au);

        sys.ApplyRetailView(
            new Vector3(2f, 0f, 0f),
            new HashSet<uint> { 0x0101000Au },
            true);
        sys.Tick(1f);

        Assert.Equal(1, sys.ActiveEmitterCount);
        Assert.True(Assert.Single(sys.EnumerateEmitters()).Finished);

        sys.Tick(0.01f);

        Assert.Equal(0, sys.ActiveEmitterCount);
    }

    [Fact]
    public void FiniteEmitter_DegradedBranchDoesNotStopBeforeAuthoredLimit()
    {
        var sys = MakeSystem();
        int handle = sys.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x3200007Du,
                Type = ParticleType.Still,
                EmitterKind = ParticleEmitterKind.BirthratePerSec,
                Birthrate = 10f,
                MaxDegradeDistance = 1f,
                MaxParticles = 4,
                InitialParticles = 1,
                TotalParticles = 4,
                TotalDuration = 100f,
                LifetimeMin = 50f,
                LifetimeMax = 50f,
            },
            Vector3.Zero,
            attachedObjectId: 15u);
        sys.UpdateEmitterOwnerCell(handle, 0x0101000Fu);
        sys.ApplyRetailView(
            new Vector3(2f, 0f, 0f),
            new HashSet<uint> { 0x0101000Fu },
            true);

        sys.Tick(1f);

        ParticleEmitter emitter = Assert.Single(sys.EnumerateEmitters());
        Assert.False(emitter.Finished);
        Assert.Equal(1, emitter.ActiveCount);
        Assert.Equal(1, emitter.TotalEmitted);
    }

    [Fact]
    public void FiniteEmitter_DegradedDueEmissionRecordsBothRetailCounters()
    {
        var sys = MakeSystem();
        int handle = sys.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x3200007Eu,
                Type = ParticleType.Still,
                EmitterKind = ParticleEmitterKind.BirthratePerSec,
                Birthrate = 0.5f,
                MaxDegradeDistance = 1f,
                MaxParticles = 4,
                TotalParticles = 3,
                TotalDuration = 100f,
                LifetimeMin = 5f,
                LifetimeMax = 5f,
            },
            Vector3.Zero,
            attachedObjectId: 16u);
        sys.UpdateEmitterOwnerCell(handle, 0x01010010u);
        sys.ApplyRetailView(
            new Vector3(2f, 0f, 0f),
            new HashSet<uint> { 0x01010010u },
            true);

        sys.Tick(0.25f);
        ParticleEmitter beforeDue = Assert.Single(sys.EnumerateEmitters());
        Assert.Equal(0, beforeDue.ActiveCount);
        Assert.Equal(0, beforeDue.TotalEmitted);
        Assert.False(beforeDue.Finished);

        sys.Tick(0.5f);
        ParticleEmitter afterDue = Assert.Single(sys.EnumerateEmitters());
        Assert.Equal(1, afterDue.ActiveCount);
        Assert.Equal(1, afterDue.TotalEmitted);
        Assert.False(afterDue.Finished);
        Assert.Empty(sys.EnumerateLive());
    }

    [Fact]
    public void FiniteEmitter_DegradedRecordedCountPersistsUntilOwnerTeardownLikeRetail()
    {
        var sys = MakeSystem();
        int handle = sys.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x3200007Fu,
                Type = ParticleType.Still,
                EmitterKind = ParticleEmitterKind.BirthratePerSec,
                Birthrate = 0.5f,
                MaxDegradeDistance = 1f,
                MaxParticles = 1,
                TotalParticles = 1,
                TotalDuration = 100f,
                LifetimeMin = 5f,
                LifetimeMax = 5f,
            },
            Vector3.Zero,
            attachedObjectId: 17u);
        sys.UpdateEmitterOwnerCell(handle, 0x01010011u);
        sys.ApplyRetailView(
            new Vector3(2f, 0f, 0f),
            new HashSet<uint> { 0x01010011u },
            true);

        sys.Tick(0.75f);
        ParticleEmitter stopped = Assert.Single(sys.EnumerateEmitters());
        Assert.True(stopped.Finished);
        Assert.Equal(1, stopped.ActiveCount);
        Assert.Empty(sys.EnumerateLive());

        sys.Tick(10f);
        Assert.True(sys.IsEmitterAlive(handle));
        Assert.Equal(1, Assert.Single(sys.EnumerateEmitters()).ActiveCount);
        Assert.Empty(sys.EnumerateLive());

        sys.StopEmitter(handle, fadeOut: false);
        Assert.False(sys.IsEmitterAlive(handle));
    }

    [Fact]
    public void UpdateEmitterAnchor_AttachLocal_ParticlePositionFollowsLiveAnchor()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32AABBCCu,
            Type = ParticleType.Still,
            Flags = EmitterFlags.AttachLocal | EmitterFlags.Billboard,
            MaxParticles = 1,
            InitialParticles = 1,
            LifetimeMin = 100f,
            LifetimeMax = 100f,
            Lifespan = 100f,
            StartSize = 1f,
            EndSize = 1f,
            StartAlpha = 1f,
            EndAlpha = 1f,
            // Zero motion + zero offset so position == origin == AnchorPos.
        };
        int handle = sys.SpawnEmitter(desc, anchor: new Vector3(10, 0, 0));
        sys.Tick(0.01f);

        var p1 = sys.EnumerateLive().Single().Emitter.Particles[0];
        Assert.Equal(new Vector3(10, 0, 0), p1.Position);

        // Move the live anchor; AttachLocal should track it on the next tick.
        sys.UpdateEmitterAnchor(handle, new Vector3(50, 20, 5));
        sys.Tick(0.01f);

        var p2 = sys.EnumerateLive().Single().Emitter.Particles[0];
        Assert.Equal(new Vector3(50, 20, 5), p2.Position);
    }

    [Fact]
    public void UpdateEmitterAnchor_AttachLocalCleared_ParticleFrozenAtSpawnOrigin()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32AABBCDu,
            Type = ParticleType.Still,
            Flags = EmitterFlags.Billboard,    // NO AttachLocal
            MaxParticles = 1,
            InitialParticles = 1,
            LifetimeMin = 100f,
            LifetimeMax = 100f,
            Lifespan = 100f,
            StartSize = 1f,
            EndSize = 1f,
            StartAlpha = 1f,
            EndAlpha = 1f,
        };
        int handle = sys.SpawnEmitter(desc, anchor: new Vector3(10, 0, 0));
        sys.Tick(0.01f);

        sys.UpdateEmitterAnchor(handle, new Vector3(99, 99, 99));
        sys.Tick(0.01f);

        var p = sys.EnumerateLive().Single().Emitter.Particles[0];
        Assert.Equal(new Vector3(10, 0, 0), p.Position);
    }

    [Fact]
    public void EmitterDied_FiresOncePerHandle_AfterAllParticlesExpire()
    {
        var sys = MakeSystem();
        var fired = new System.Collections.Generic.List<int>();
        sys.EmitterDied += h => fired.Add(h);

        int handle = sys.SpawnEmitter(MakeDesc(emitRate: 5f, lifetime: 0.2f, maxParticles: 4), Vector3.Zero);
        sys.StopEmitter(handle, fadeOut: false);   // kill emitter + all particles immediately
        sys.Tick(0.01f);

        Assert.Single(fired);
        Assert.Equal(handle, fired[0]);
        Assert.False(sys.IsEmitterAlive(handle));
    }

    [Fact]
    public void EmitterDied_AttemptsEverySubscriberWhenOneThrows()
    {
        var sys = MakeSystem();
        var delivered = new List<int>();
        sys.EmitterDied += _ => throw new InvalidOperationException("first subscriber failed");
        sys.EmitterDied += delivered.Add;

        int handle = sys.SpawnEmitter(
            MakeDesc(emitRate: 5f, lifetime: 0.2f, maxParticles: 4),
            Vector3.Zero);

        AggregateException failure = Assert.Throws<AggregateException>(
            () => sys.StopEmitter(handle, fadeOut: false));

        Assert.Contains("first subscriber failed", failure.ToString());
        Assert.Equal([handle], delivered);
        Assert.False(sys.IsEmitterAlive(handle));
    }

    [Fact]
    public void Birthrate_PerSec_EmitsOnePerTickWhenIntervalElapsed()
    {
        var sys = MakeSystem();
        var desc = new EmitterDesc
        {
            DatId = 0x32AAAA01u,
            Type = ParticleType.Still,
            EmitterKind = ParticleEmitterKind.BirthratePerSec,
            Birthrate = 0.05f,         // 50ms minimum between emits
            EmitRate = 0f,             // disable the EmitRate fallback path
            MaxParticles = 100,
            LifetimeMin = 100f,
            LifetimeMax = 100f,
            Lifespan = 100f,
            StartSize = 1f,
            EndSize = 1f,
            StartAlpha = 1f,
            EndAlpha = 1f,
        };
        sys.SpawnEmitter(desc, Vector3.Zero);

        sys.Tick(1.0f);
        Assert.Equal(1, sys.ActiveParticleCount);

        // Subsequent small ticks each emit once if birthrate has elapsed.
        sys.Tick(0.06f);  // > 0.05s since last emit
        Assert.Equal(2, sys.ActiveParticleCount);

        // A tick smaller than birthrate adds nothing.
        sys.Tick(0.01f);
        Assert.Equal(2, sys.ActiveParticleCount);
    }

    private sealed class FakeDatObjectSource : IDatObjectSource
    {
        private readonly Dictionary<uint, IDBObj> _objects = new();
        private readonly Dictionary<uint, int> _calls = new();

        public void Add(uint id, IDBObj value) => _objects[id] = value;

        public int GetCallCount(uint id) =>
            _calls.TryGetValue(id, out int count) ? count : 0;

        public T Get<T>(uint fileId) where T : IDBObj
        {
            _calls.TryGetValue(fileId, out int count);
            _calls[fileId] = count + 1;
            return _objects.TryGetValue(fileId, out IDBObj? value)
                && value is T typed
                    ? typed
                    : default!;
        }

        public bool TryGet<T>(uint fileId, out T value) where T : IDBObj
        {
            value = Get<T>(fileId);
            return value is not null;
        }
    }
}
