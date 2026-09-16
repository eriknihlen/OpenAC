using System.Numerics;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Tests.UI.Layout;
using AcDream.Content;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit.Abstractions;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;
using VfxParticleEmitter = AcDream.Core.Vfx.ParticleEmitter;

namespace AcDream.App.Tests.Rendering.Vfx;

/// <summary>
/// A placement whose only authored content is a default script draws no
/// geometry of its own, so its hydrated entity carries no mesh refs. Its
/// script still parents an emitter to a part of the placement's setup by
/// index, and that must resolve: a hook parents by part index, and whether
/// the part draws is a separate question from whether it exists.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class InteriorEffectPlacementEmissionInstalledDatTests
{
    /// <summary>The ground-haze placement inside the reported interior cell.</summary>
    private const uint GroundHazePlacement = 0x020003FFu;

    private const uint ReportedCell = 0x0108020Du;

    private static readonly Vector3 PlacementOrigin = new(49.711f, -52.9128f, 0f);

    private readonly ITestOutputHelper _output;

    public InteriorEffectPlacementEmissionInstalledDatTests(ITestOutputHelper output) =>
        _output = output;

    [InstalledDatFact]
    public void TheGroundHazePlacementEmitsParticlesFromItsDefaultScript()
    {
        using var scene = new Scene(GroundHazePlacement);

        _output.WriteLine(
            $"placement 0x{GroundHazePlacement:X8} parts={scene.Setup.Parts.Count} " +
            $"meshRefs={scene.Entity.MeshRefs.Count} " +
            $"script=0x{scene.Setup.DefaultScript.DataId:X8}");

        Assert.Empty(scene.Entity.MeshRefs);
        Assert.NotEqual(0u, scene.Setup.DefaultScript.DataId);

        Assert.True(
            scene.Runner.Play(
                scene.Setup.DefaultScript.DataId,
                scene.Entity.Id,
                scene.Entity.Position),
            "the placement's default script must start");
        scene.Runner.Tick(1f / 60f);

        _output.WriteLine($"diagnostics: {string.Join(" | ", scene.Diagnostics)}");

        Assert.Equal(1, scene.System.ActiveEmitterCount);

        // The emitter belongs to the cell the placement lives in, so the
        // interior render scope can find it.
        VfxParticleEmitter spawned = Assert.Single(scene.System.EnumerateEmitters());
        Assert.Equal(ReportedCell, spawned.OwnerCellId);

        for (int frame = 0; frame < 120; frame++)
            scene.System.Tick(1f / 60f);

        int live = 0;
        float maxAlpha = 0f;
        float maxSize = 0f;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach ((VfxParticleEmitter emitter, int index) in scene.System.EnumerateLive())
        {
            ref Particle particle = ref emitter.Particles[index];
            live++;
            min = Vector3.Min(min, particle.Position);
            max = Vector3.Max(max, particle.Position);
            maxSize = MathF.Max(maxSize, particle.Size);
            maxAlpha = MathF.Max(maxAlpha, particle.StartAlpha);
        }

        _output.WriteLine(
            $"live={live} pos=[{min:F2}]..[{max:F2}] maxSize={maxSize:F2} maxAlpha={maxAlpha:F3}");

        Assert.True(live > 0, "the emitter must have live particles");
        Assert.True(maxAlpha > 0f, "particles must be born with visible opacity");
        Assert.True(maxSize > 0f, "particles must be born with a visible size");

        // Born around the placement, not at the world origin or far away.
        Assert.True(
            Vector3.Distance(min, PlacementOrigin) < 8f
                && Vector3.Distance(max, PlacementOrigin) < 8f,
            $"particles must be born near the placement at {PlacementOrigin}");
    }

    private sealed class Scene : IDisposable
    {
        private readonly DatCollection _dats;

        internal Setup Setup { get; }
        internal WorldEntity Entity { get; }
        internal ParticleSystem System { get; }
        internal PhysicsScriptRunner Runner { get; }
        internal List<string> Diagnostics { get; } = [];

        internal Scene(uint placementSetupId)
        {
            string? directory = InstalledDatTestPath.Resolve();
            Assert.True(
                Directory.Exists(directory),
                "An installed content directory is required.");
            _dats = new DatCollection(directory!, DatAccessType.Read);
            var adapter = new DatCollectionAdapter(_dats);
            Setup = Assert.IsType<Setup>(adapter.Get<Setup>(placementSetupId));

            // Exactly what interior hydration produces for this placement:
            // every part is a runtime-hidden marker, so no mesh refs survive.
            Entity = new WorldEntity
            {
                Id = 0x4001_0001u,
                SourceGfxObjOrSetupId = placementSetupId,
                Position = PlacementOrigin,
                Rotation = Quaternion.Identity,
                MeshRefs = [],
                ParentCellId = ReportedCell,
            };
            (Matrix4x4[] poses, bool[] available) =
                IndexedSetupPartPoseBuilder.Build(Setup, Entity);
            Entity.SetIndexedPartPoses(poses, available);

            var registry = new EmitterDescRegistry(adapter);
            System = new ParticleSystem(registry, new Random(1234));
            var poseRegistry = new EntityEffectPoseRegistry();
            poseRegistry.Publish(Entity, poses, available);
            var sink = new ParticleHookSink(System, poseRegistry)
            {
                DiagnosticSink = Diagnostics.Add,
            };
            Runner = new PhysicsScriptRunner(
                id => adapter.Get<DatPhysicsScript>(id),
                sink);
        }

        public void Dispose() => _dats.Dispose();
    }
}
