using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.Content.Vfx;
using AcDream.Core.Meshing;
using AcDream.Core.Vfx;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Rendering;

public sealed class RetailParticleGeometryClassifierTests
{
    [Theory]
    [InlineData(null, RetailParticleGeometryKind.FullMesh)]
    [InlineData(1, RetailParticleGeometryKind.FullMesh)]
    [InlineData(0, RetailParticleGeometryKind.Billboard)]
    [InlineData(2, RetailParticleGeometryKind.Billboard)]
    [InlineData(3, RetailParticleGeometryKind.Billboard)]
    public void Classify_matchesRetailAlways2D(
        int? firstDegradeMode,
        RetailParticleGeometryKind expected)
    {
        Assert.Equal(
            expected,
            RetailParticleGeometryClassifier.Classify(
                firstDegradeMode is int mode ? (uint)mode : null));
    }

    [Fact]
    public void Classify_unknownUnsignedMode_isBillboardWithoutOverflow()
        => Assert.Equal(
            RetailParticleGeometryKind.Billboard,
            RetailParticleGeometryClassifier.Classify(uint.MaxValue));

    [Fact]
    public void SubmissionOrdering_interleavesGeometryByDistanceAndPreservesEqualDistanceSequence()
    {
        var submissions = new List<ParticleSubmission>
        {
            new(ParticleSubmissionKind.Mesh, 0, 4f, 0),
            new(ParticleSubmissionKind.Billboard, 0, 9f, 1),
            new(ParticleSubmissionKind.Mesh, 1, 9f, 2),
            new(ParticleSubmissionKind.Billboard, 1, 1f, 3),
        };

        ParticleSubmissionOrdering.Sort(submissions);

        Assert.Equal(
            new[]
            {
                (ParticleSubmissionKind.Billboard, 1),
                (ParticleSubmissionKind.Mesh, 2),
                (ParticleSubmissionKind.Mesh, 0),
                (ParticleSubmissionKind.Billboard, 3),
            },
            submissions.Select(static item => (item.Kind, item.Sequence)));
    }

    [Fact]
    public void MeshReferenceTracker_balancesDuplicateReleaseAndDispose()
    {
        var increments = new List<uint>();
        var decrements = new List<uint>();
        var tracker = new ParticleMeshReferenceTracker(increments.Add, decrements.Add);

        tracker.Register(7, 0x01001666u);
        tracker.Register(7, 0x01001666u);
        tracker.Register(8, 0x01001667u);
        tracker.Release(7);
        tracker.Release(7);
        tracker.Dispose();
        tracker.Dispose();

        Assert.Equal(new[] { 0x01001666u, 0x01001667u }, increments);
        Assert.Equal(new[] { 0x01001666u, 0x01001667u }, decrements);
    }

    [Fact]
    public void MeshReferenceTracker_retriesAcquireThatFailedBeforeCommit()
    {
        int attempts = 0;
        var increments = new List<uint>();
        using var tracker = new ParticleMeshReferenceTracker(
            id =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("before commit");
                increments.Add(id);
            },
            _ => { });

        Assert.Throws<InvalidOperationException>(() => tracker.Register(7, 0x01001666u));
        tracker.Register(7, 0x01001666u);

        Assert.Equal([0x01001666u], increments);
    }

    [Fact]
    public void MeshReferenceTracker_doesNotRepeatAcquireThatCommittedBeforeThrowing()
    {
        int attempts = 0;
        using var tracker = new ParticleMeshReferenceTracker(
            _ =>
            {
                attempts++;
                throw new MeshReferenceMutationException(
                    "after commit",
                    mutationCommitted: true,
                    new InvalidOperationException());
            },
            _ => { });

        Assert.Throws<MeshReferenceMutationException>(() => tracker.Register(7, 0x01001666u));
        tracker.Register(7, 0x01001666u);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void MeshReferenceTracker_retriesReleaseWithoutRepeatingCommittedMutation()
    {
        int attempts = 0;
        var tracker = new ParticleMeshReferenceTracker(_ => { }, _ =>
        {
            attempts++;
            if (attempts == 1)
                throw new InvalidOperationException("before commit");
        });
        tracker.Register(7, 0x01001666u);

        Assert.Throws<InvalidOperationException>(() => tracker.Release(7));
        tracker.Release(7);
        tracker.Release(7);

        Assert.Equal(2, attempts);
        tracker.Dispose();
    }

    [Fact]
    public void MeshReferenceTracker_disposeAttemptsEveryOwnerAndCanResume()
    {
        bool failFirst = true;
        var releases = new List<uint>();
        var tracker = new ParticleMeshReferenceTracker(_ => { }, id =>
        {
            if (id == 0x01001666u && failFirst)
            {
                failFirst = false;
                throw new InvalidOperationException("before commit");
            }
            releases.Add(id);
        });
        tracker.Register(7, 0x01001666u);
        tracker.Register(8, 0x01001667u);

        Assert.Throws<AggregateException>(() => tracker.Dispose());
        Assert.Contains(0x01001667u, releases);
        tracker.Dispose();

        Assert.Equal(1, releases.Count(id => id == 0x01001666u));
        Assert.Equal(1, releases.Count(id => id == 0x01001667u));
    }

    [Fact]
    public void MeshReferenceTracker_releaseReentryDuringAcquireReconcilesWithoutLeak()
    {
        ParticleMeshReferenceTracker? tracker = null;
        int increments = 0;
        int decrements = 0;
        tracker = new ParticleMeshReferenceTracker(
            _ =>
            {
                increments++;
                tracker!.Release(7);
            },
            _ => decrements++);

        tracker.Register(7, 0x01001666u);
        tracker.Release(7);
        tracker.Dispose();

        Assert.Equal(1, increments);
        Assert.Equal(1, decrements);
    }

    [Fact]
    public void BlendResolver_corruptSurfaceFallsBackAndReportsItsDid()
    {
        var diagnostics = new List<string>();

        TranslucencyKind blend = RetailParticleBlendResolver.Resolve(
            0x08001234u,
            batchIsAdditive: true,
            _ => throw new InvalidDataException("bad surface"),
            diagnostics.Add);

        Assert.Equal(TranslucencyKind.Additive, blend);
        string diagnostic = Assert.Single(diagnostics);
        Assert.Contains("0x08001234", diagnostic, StringComparison.Ordinal);
        Assert.Contains("bad surface", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledArmorSelfShieldPieces_areModeOneMeshesWithAuthoredApexFaces()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            throw new InvalidOperationException(
                "Lane=InstalledDat requires client_portal.dat for the Armor Self asset-chain gate; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var tableResolver = new PhysicsScriptTableResolver(
            id => dats.Get<PhysicsScriptTable>(id));
        uint? scriptDid = tableResolver.Resolve(
            0x34000004u,
            rawScriptType: 0x37u, // PS_ShieldUpGrey / TargetEffect 55
            intensity: 1f);
        Assert.Equal(0x3300068Fu, scriptDid);

        var scriptLoader = new RetailPhysicsScriptLoader(dats);
        DatReaderWriter.DBObjs.PhysicsScript script = Assert.IsType<DatReaderWriter.DBObjs.PhysicsScript>(
            scriptLoader.LoadPhysicsScript(scriptDid!.Value));
        uint[] emitterIds = script.ScriptData
            .Select(static item => item.Hook)
            .OfType<CreateParticleHook>()
            .Select(static hook => hook.EmitterInfoId.DataId)
            .ToArray();
        Assert.Equal(
            new[] { 0x32000350u, 0x32000351u, 0x32000355u, 0x32000356u, 0x32000379u },
            emitterIds);

        var emitters = new EmitterDescRegistry(dats);
        uint[] greyShieldPieces = emitterIds
            .Select(id => emitters.Get(id).HwGfxObjId)
            .Take(4)
            .ToArray();
        Assert.Equal(
            new[] { 0x01001666u, 0x01001667u, 0x01001668u, 0x01001669u },
            greyShieldPieces);
        Assert.Equal(0x01001098u, emitters.Get(emitterIds[4]).HwGfxObjId);

        foreach (uint gfxObjId in greyShieldPieces)
        {
            GfxObj gfx = Assert.IsType<GfxObj>(dats.Get<GfxObj>(gfxObjId));
            Assert.Equal(5, gfx.VertexArray.Vertices.Count);
            Assert.True(gfx.Flags.HasFlag(GfxObjFlags.HasDIDDegrade));

            GfxObjDegradeInfo degrade = Assert.IsType<GfxObjDegradeInfo>(
                dats.Get<GfxObjDegradeInfo>(gfx.DIDDegrade));
            Assert.NotEmpty(degrade.Degrades);
            Assert.Equal(1u, degrade.Degrades[0].DegradeMode);

            var apex = gfx.VertexArray.Vertices.MaxBy(static pair => pair.Value.Origin.Z);
            Assert.InRange(apex.Value.Origin.Z, 2.31f, 2.33f);
            Assert.Contains(
                gfx.Polygons.Values,
                polygon => polygon.VertexIds.Any(id => Convert.ToUInt16(id) == apex.Key));
        }
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledHumanEffectTable_MapsPortalVisibilityScripts()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            throw new InvalidOperationException(
                "Lane=InstalledDat requires client_portal.dat; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var resolver = new PhysicsScriptTableResolver(id => dats.Get<PhysicsScriptTable>(id));
        Assert.Equal(0x33000332u, resolver.Resolve(0x34000004u, 0x74u, 1f));
        Assert.Equal(0x3300032Fu, resolver.Resolve(0x34000004u, 0x75u, 1f));
        Assert.Equal(0x33000331u, resolver.Resolve(0x34000004u, 0x76u, 1f));
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledAerlintheSwarmEmitter_UsesItsHardwareGfxDegradeDistance()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            throw new InvalidOperationException(
                "Lane=InstalledDat requires client_portal.dat; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var emitters = new EmitterDescRegistry(dats);
        EmitterDesc desc = emitters.Get(0x32000223u);
        GfxObj gfx = Assert.IsType<GfxObj>(dats.Get<GfxObj>(desc.HwGfxObjId));
        GfxObjDegradeInfo degrade = Assert.IsType<GfxObjDegradeInfo>(
            dats.Get<GfxObjDegradeInfo>(gfx.DIDDegrade));

        Assert.Equal(
            RetailParticleDegradeDistance.FromEntries(degrade.Degrades),
            desc.MaxDegradeDistance);
        Assert.Equal(64f, desc.MaxDegradeDistance);
    }

    private static string? ResolveDatDir()
    {
        string? configured = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(configured)
            && File.Exists(Path.Combine(configured, "client_portal.dat")))
            return configured;

        const string installed = @"C:\Turbine\Asheron's Call";
        if (File.Exists(Path.Combine(installed, "client_portal.dat")))
            return installed;

        string conventional = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
        return File.Exists(Path.Combine(conventional, "client_portal.dat"))
            ? conventional
            : null;
    }
}
