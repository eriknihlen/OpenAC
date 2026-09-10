using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using DatReaderWriter.Types;
using Xunit;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;

namespace AcDream.App.Tests.Rendering;

public sealed class SkyPesFrameControllerTests
{
    private const uint AuroraSetup = 0x02000714u;
    private const uint AuroraScript = 0x330007DBu;
    private const uint LightningEmitter = 0x320002C2u;

    private sealed class Harness
    {
        private sealed class RecordingHookSink : IAnimationHookSink
        {
            public List<(uint EntityId, Vector3 Position, AnimationHook Hook)> Calls { get; } = [];

            public void OnHook(
                uint entityId,
                Vector3 entityWorldPosition,
                AnimationHook hook) =>
                Calls.Add((entityId, entityWorldPosition, hook));
        }

        public readonly List<uint> ResolvedScriptIds = [];
        public readonly List<string> Diagnostics = [];
        public readonly List<uint> StoppedAudioOwners = [];
        public readonly List<(uint EntityId, Vector3 Position, AnimationHook Hook)> HookCalls;
        public readonly PhysicsScriptRunner Runner;
        public readonly SkyPesFrameController Controller;
        public readonly ParticleSystem Particles;

        public Harness(AnimationHook? hook = null, double hookTime = 0.0)
        {
            var registry = new EmitterDescRegistry();
            registry.Register(new EmitterDesc
            {
                DatId = LightningEmitter,
                Type = ParticleType.Still,
                Flags = EmitterFlags.Billboard,
                EmitterKind = ParticleEmitterKind.BirthratePerSec,
                MaxParticles = 2,
                InitialParticles = 1,
                LifetimeMin = 0.01f,
                LifetimeMax = 0.01f,
                Lifespan = 0.01f,
                StartSize = 1f,
                EndSize = 1f,
                StartAlpha = 1f,
                EndAlpha = 1f,
                Birthrate = 1000f,
            });
            Particles = new ParticleSystem(registry, new Random(42));
            var poses = new EntityEffectPoseRegistry();
            var sink = new ParticleHookSink(Particles, poses)
            {
                DiagnosticSink = Diagnostics.Add,
            };
            var recording = new RecordingHookSink();
            HookCalls = recording.Calls;
            var router = new AnimationHookRouter();
            router.Register(sink);
            router.Register(recording);
            Runner = new PhysicsScriptRunner(
                id =>
                {
                    ResolvedScriptIds.Add(id);
                    var script = new DatPhysicsScript();
                    script.ScriptData.Add(new PhysicsScriptData
                    {
                        StartTime = hookTime,
                        Hook = hook ?? new SoundHook(),
                    });
                    return script;
                },
                router);
            Controller = new SkyPesFrameController(
                Runner,
                sink,
                poses,
                effects: null,
                Diagnostics.Add,
                StoppedAudioOwners.Add);
        }
    }

    private static SkyObjectData Carrier(
        uint gfxObjId = AuroraSetup,
        uint scriptId = AuroraScript,
        uint pesColumn = AuroraScript,
        uint properties = 0u,
        float begin = 0f,
        float end = 0f) => new()
    {
        GfxObjId = gfxObjId,
        DefaultScriptId = scriptId,
        PesObjectId = pesColumn,
        Properties = properties,
        BeginTime = begin,
        EndTime = end,
    };

    private static DayGroupData Group(params SkyObjectData[] objects) => new()
    {
        Name = "Test",
        SkyObjects = objects,
    };

    [Fact]
    public void PlaysOncePerSlotAndPersistsAcrossEquivalentDayGroups()
    {
        var h = new Harness();

        h.Controller.Update(0.1f, Group(Carrier()), Vector3.Zero);
        Assert.Equal([AuroraScript], h.ResolvedScriptIds);
        Assert.Equal(1, h.Runner.ActiveScriptCount);

        h.Controller.Update(0.9f, Group(Carrier()), Vector3.One * 10f);
        Assert.Equal(1, h.Runner.ActiveScriptCount);
    }

    [Fact]
    public void SlotIdentityChangeStopsTheOldScriptAndPlaysTheNew()
    {
        var h = new Harness();
        h.Controller.Update(0.1f, Group(Carrier()), Vector3.Zero);

        h.Controller.Update(
            0.1f,
            Group(Carrier(gfxObjId: 0x02000589u, scriptId: 0x3300042Cu, pesColumn: 0x3300042Cu)),
            Vector3.Zero);

        Assert.Equal([AuroraScript, 0x3300042Cu], h.ResolvedScriptIds);
        Assert.Equal(1, h.Runner.ActiveScriptCount);
    }

    [Fact]
    public void LeavingTheVisibilityWindowStopsTheScriptAndReentryReplays()
    {
        var h = new Harness();
        SkyObjectData windowed() => Carrier(begin: 0.2f, end: 0.4f);

        h.Controller.Update(0.3f, Group(windowed()), Vector3.Zero);
        Assert.Equal(1, h.Runner.ActiveScriptCount);

        h.Controller.Update(0.5f, Group(windowed()), Vector3.Zero);
        Assert.Equal(0, h.Runner.ActiveScriptCount);

        h.Controller.Update(0.25f, Group(windowed()), Vector3.Zero);
        Assert.Equal(1, h.Runner.ActiveScriptCount);
    }

    [Fact]
    public void SetupDefaultScriptIsTheSourceAndAMismatchedPesColumnLogsOnce()
    {
        var h = new Harness();
        var group = Group(Carrier(scriptId: AuroraScript, pesColumn: 0x33000999u));

        h.Controller.Update(0.1f, group, Vector3.Zero);
        h.Controller.Update(0.2f, group, Vector3.Zero);

        Assert.Equal([AuroraScript], h.ResolvedScriptIds);
        string diagnostic = Assert.Single(h.Diagnostics);
        Assert.Contains("0x330007DB", diagnostic, StringComparison.Ordinal);
        Assert.Contains("0x33000999", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void CarriersWithoutADefaultScriptNeverPlay()
    {
        var h = new Harness();
        h.Controller.Update(
            0.1f,
            Group(Carrier(gfxObjId: 0x010015F0u, scriptId: 0u, pesColumn: 0u)),
            Vector3.Zero);

        Assert.Empty(h.ResolvedScriptIds);
        Assert.Equal(0, h.Runner.ActiveScriptCount);
    }

    [Fact]
    public void NullDayGroupStopsEverything()
    {
        var h = new Harness();
        h.Controller.Update(0.1f, Group(Carrier()), Vector3.Zero);
        Assert.Equal(1, h.Runner.ActiveScriptCount);

        h.Controller.Update(0.1f, null, Vector3.Zero);
        Assert.Equal(0, h.Runner.ActiveScriptCount);
    }

    [Fact]
    public void LightningPartZeroUsesTheLiveCarrierPoseAndStopsOnDayGroupFlip()
    {
        var create = new CreateParticleHook
        {
            EmitterInfoId = LightningEmitter,
            EmitterId = 1u,
            PartIndex = 0u,
            Offset = new Frame(),
        };
        var h = new Harness(create);

        h.Controller.Update(0.1f, Group(Carrier()), new Vector3(4f, 5f, 6f));
        h.Runner.Tick(0.0);

        Assert.Equal(1, h.Particles.ActiveEmitterCount);
        Assert.Empty(h.Diagnostics);

        h.Controller.Update(0.1f, null, Vector3.Zero);
        h.Runner.Tick(1.0);

        Assert.Equal(0, h.Runner.ActiveScriptCount);
        Assert.Empty(h.Diagnostics);
    }

    [Fact]
    public void PersistentWeatherCarrier_RefreshesSoundAnchorToCurrentCamera()
    {
        var sound = new SoundTweakedHook
        {
            SoundId = 0x0A00038Bu,
            Volume = 0.1f,
            Priority = 1f,
        };
        var h = new Harness(sound, hookTime: 1.0);
        var initialCamera = new Vector3(10f, 20f, 30f);
        var currentCamera = new Vector3(410f, 520f, 630f);

        h.Controller.Update(0.3f, Group(Carrier()), initialCamera);
        h.Controller.Update(0.4f, Group(Carrier()), currentCamera);
        h.Runner.Tick(1.0);

        var call = Assert.Single(h.HookCalls);
        Assert.Same(sound, call.Hook);
        Assert.Equal(currentCamera, call.Position);
        Assert.Single(h.ResolvedScriptIds);
    }

    [Fact]
    public void EnclosedFrameStopsSkyHooksAndAudioWhilePreservingOtherOwners()
    {
        var sound = new SoundTweakedHook
        {
            SoundId = 0x0A00038Bu,
            Volume = 0.1f,
            Priority = 1f,
        };
        var h = new Harness(sound, hookTime: 1.0);
        const uint otherOwner = 0x50000001u;

        h.Controller.Update(0.3f, Group(Carrier()), Vector3.Zero);
        Assert.True(h.Runner.PlayDirect(otherOwner, AuroraScript));

        h.Controller.Update(0.4f, Group(Carrier()), Vector3.Zero, skyActive: false);
        h.Runner.Tick(1.0);

        var otherCall = Assert.Single(h.HookCalls);
        Assert.Equal(otherOwner, otherCall.EntityId);
        Assert.Equal([0xF0000000u], h.StoppedAudioOwners);
        Assert.Equal(0, h.Runner.ActiveScriptCount);

        h.Controller.Update(0.5f, Group(Carrier()), Vector3.One, skyActive: true);
        h.Runner.Tick(2.0);

        Assert.Equal(2, h.HookCalls.Count);
        Assert.Equal(0xF0000000u, h.HookCalls[1].EntityId);
    }
}
