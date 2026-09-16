using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Tests.UI.Layout;
using AcDream.Content.CharGen;
using AcDream.Content.Vfx;
using AcDream.Core.CharGen;
using AcDream.Core.World;
using DatReaderWriter;
using Animation = DatReaderWriter.DBObjs.Animation;
using Setup = DatReaderWriter.DBObjs.Setup;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.Rendering;

/// <summary>
/// The doll pose is an authored animation applied part-by-part to the
/// character's setup, so it only makes sense if the animation was authored for
/// that setup. This lane checks that against the installed data rather than
/// against a constant we wrote down: every heritage's body and its doll pose
/// have to agree on how many parts there are. Issue #98 was the humanoid pose
/// (34 parts) landing on the two Olthoi bodies (25 and 31), which gave each
/// Olthoi part the placement of whatever humanoid limb shared its index.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class PaperdollHeritagePoseInstalledDatTests
{
    private static string DatDirectory =>
        Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");

    [InstalledDatFact]
    public void EveryHeritagePose_HasOneFramePerPartOfThatHeritagesBody()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        ChargenOptions options = ChargenTableReader.Load(dats);
        Assert.NotEmpty(options.HeritagesById);

        foreach ((uint heritageId, ChargenHeritageOptions heritage)
            in options.HeritagesById.OrderBy(entry => entry.Key))
        {
            uint poseEnum = PaperdollHeritagePresentation.ResolvePoseEnum(heritageId);
            uint poseDid = RetailHeldPose.ResolvePoseDid(dats, poseEnum);
            Assert.True(
                (poseDid >> 24) == 0x03u,
                $"heritage {heritageId} ({heritage.Name}) pose 0x{poseEnum:X8} "
                + $"did not resolve to an animation (got 0x{poseDid:X8}).");

            Animation? pose = dats.Portal.Get<Animation>(poseDid);
            Assert.NotNull(pose);
            Assert.NotEmpty(pose!.PartFrames);
            int poseParts = pose.PartFrames[^1].Frames.Count;

            foreach (ChargenGenderOptions gender in heritage.GendersByKey.Values)
            {
                Setup? body = dats.Portal.Get<Setup>(gender.SetupId);
                Assert.NotNull(body);
                Assert.True(
                    poseParts == body!.Parts.Count,
                    $"heritage {heritageId} ({heritage.Name}) body "
                    + $"0x{gender.SetupId:X8} has {body.Parts.Count} parts but its "
                    + $"doll pose 0x{poseDid:X8} places {poseParts}.");
            }
        }
    }

    /// <summary>
    /// The mismatch the fix removes, stated directly: the default pose does not
    /// fit either Olthoi body, so a single shared pose cannot be correct.
    /// </summary>
    [InstalledDatFact]
    public void TheDefaultPose_DoesNotFitEitherOlthoiBody()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        ChargenOptions options = ChargenTableReader.Load(dats);

        uint defaultDid = RetailHeldPose.ResolvePoseDid(
            dats, PaperdollHeritagePresentation.DefaultPoseEnum);
        Animation? defaultPose = dats.Portal.Get<Animation>(defaultDid);
        Assert.NotNull(defaultPose);
        int defaultParts = defaultPose!.PartFrames[^1].Frames.Count;

        foreach (uint heritageId in new[]
        {
            (uint)ChargenHeritageGroup.Olthoi,
            (uint)ChargenHeritageGroup.OlthoiAcid,
        })
        {
            Assert.True(
                options.HeritagesById.TryGetValue(heritageId, out ChargenHeritageOptions? heritage),
                $"heritage {heritageId} is not in the installed character table.");
            foreach (ChargenGenderOptions gender in heritage!.GendersByKey.Values)
            {
                Setup? body = dats.Portal.Get<Setup>(gender.SetupId);
                Assert.NotNull(body);
                Assert.NotEqual(defaultParts, body!.Parts.Count);
            }
        }
    }

    /// <summary>
    /// A body that wears a size of its own has to be posed at that size. The
    /// pose rewrites every part placement from the authored animation, so
    /// unless the size is re-applied there the doll comes out the size of a
    /// default body: the Lugian too small, the Olthoi too large. Both halves
    /// are checked — the part's own size and how far it sits from the body's
    /// centre — because scaling only one of them pulls the figure apart.
    /// </summary>
    [InstalledDatFact]
    public void AScaledBody_PosesEveryDollPartAtThatSize()
    {
        const float ObjectScale = 0.6f;
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        ChargenOptions options = ChargenTableReader.Load(dats);

        foreach ((uint heritageId, ChargenHeritageOptions heritage)
            in options.HeritagesById.OrderBy(entry => entry.Key))
        {
            uint setupId = heritage.GendersByKey.Values.First().SetupId;
            Setup? body = dats.Portal.Get<Setup>(setupId);
            Assert.NotNull(body);

            var applicator = new RetailPaperdollPoseApplicator(
                dats,
                new RetailAnimationLoader(dats),
                new object());

            WorldEntity plain = CreateDoll(setupId, body!.Parts.Count);
            WorldEntity scaled = CreateDoll(setupId, body.Parts.Count);
            applicator.Apply(plain, setupId, heritageId, 1f);
            applicator.Apply(scaled, setupId, heritageId, ObjectScale);

            Assert.Equal(plain.MeshRefs.Count, scaled.MeshRefs.Count);
            Assert.Contains(
                plain.MeshRefs,
                part => part.PartTransform.Translation.Length() > 0.01f);

            for (int index = 0; index < plain.MeshRefs.Count; index++)
            {
                Matrix4x4 unscaled = plain.MeshRefs[index].PartTransform;
                Matrix4x4 sized = scaled.MeshRefs[index].PartTransform;
                PaperdollFramePresenterTests.AssertClose(
                    unscaled.Translation * ObjectScale,
                    sized.Translation);
                for (int row = 0; row < 3; row++)
                {
                    PaperdollFramePresenterTests.AssertClose(
                        PaperdollFramePresenterTests.BasisRow(unscaled, row) * ObjectScale,
                        PaperdollFramePresenterTests.BasisRow(sized, row));
                }
            }
        }
    }

    private static WorldEntity CreateDoll(uint setupId, int partCount)
    {
        var parts = new List<MeshRef>(partCount);
        for (int index = 0; index < partCount; index++)
            parts.Add(new MeshRef((uint)(0x0100_0000u + index), Matrix4x4.Identity));

        return new WorldEntity
        {
            Id = 0xDA11_D012u,
            SourceGfxObjOrSetupId = setupId,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = parts,
        };
    }
}
