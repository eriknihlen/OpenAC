using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.CharGen;

namespace AcDream.App.Tests.Rendering;

public sealed class PaperdollHeritagePresentationTests
{
    [Theory]
    [InlineData(ChargenHeritageGroup.Aluvian, 0x10000005u)]
    [InlineData(ChargenHeritageGroup.Gharundim, 0x10000005u)]
    [InlineData(ChargenHeritageGroup.Sho, 0x10000005u)]
    [InlineData(ChargenHeritageGroup.Viamontian, 0x10000005u)]
    [InlineData(ChargenHeritageGroup.Shadowbound, 0x10000005u)]
    [InlineData(ChargenHeritageGroup.Gearknight, 0x10000005u)]
    [InlineData(ChargenHeritageGroup.Tumerok, 0x10000005u)]
    [InlineData(ChargenHeritageGroup.Lugian, 0x10000005u)]
    [InlineData(ChargenHeritageGroup.Empyrean, 0x10000005u)]
    [InlineData(ChargenHeritageGroup.Penumbraen, 0x10000005u)]
    [InlineData(ChargenHeritageGroup.Undead, 0x10000005u)]
    [InlineData(ChargenHeritageGroup.Olthoi, 0x10000011u)]
    [InlineData(ChargenHeritageGroup.OlthoiAcid, 0x10000013u)]
    public void PoseEnum_IsTheOneAuthoredForThatBody(
        ChargenHeritageGroup heritage, uint expected) =>
        Assert.Equal(
            expected,
            PaperdollHeritagePresentation.ResolvePoseEnum((uint)heritage));

    [Fact]
    public void UnknownHeritage_UsesTheDefaultPose() =>
        Assert.Equal(
            PaperdollHeritagePresentation.DefaultPoseEnum,
            PaperdollHeritagePresentation.ResolvePoseEnum(0u));

    [Theory]
    [InlineData(ChargenHeritageGroup.Aluvian, 0.12f, -2.4f, 0.88f)]
    [InlineData(ChargenHeritageGroup.Gharundim, 0.12f, -2.4f, 0.88f)]
    [InlineData(ChargenHeritageGroup.Sho, 0.12f, -2.4f, 0.88f)]
    [InlineData(ChargenHeritageGroup.Viamontian, 0.12f, -2.4f, 0.88f)]
    [InlineData(ChargenHeritageGroup.Shadowbound, 0.12f, -2.4f, 0.88f)]
    [InlineData(ChargenHeritageGroup.Gearknight, 0.12f, -3f, 0.88f)]
    [InlineData(ChargenHeritageGroup.Tumerok, 0.12f, -3f, 0.88f)]
    [InlineData(ChargenHeritageGroup.Lugian, 0.12f, -3.4f, 1f)]
    [InlineData(ChargenHeritageGroup.Empyrean, 0.12f, -3.4f, 0.88f)]
    [InlineData(ChargenHeritageGroup.Penumbraen, 0.12f, -2.4f, 0.88f)]
    [InlineData(ChargenHeritageGroup.Undead, 0.12f, -2.4f, 0.88f)]
    [InlineData(ChargenHeritageGroup.Olthoi, 0.12f, -3.4f, 0.88f)]
    [InlineData(ChargenHeritageGroup.OlthoiAcid, 0.12f, -3.4f, 0.88f)]
    public void Eye_IsTheOneAuthoredForThatBody(
        ChargenHeritageGroup heritage, float x, float y, float z) =>
        Assert.Equal(
            new Vector3(x, y, z),
            PaperdollHeritagePresentation.ResolveEye((uint)heritage));

    [Fact]
    public void UnknownHeritage_UsesTheDefaultEye() =>
        Assert.Equal(
            PaperdollHeritagePresentation.DefaultEye,
            PaperdollHeritagePresentation.ResolveEye(0u));

    /// <summary>
    /// Bigger bodies are viewed from further back, so the two Olthoi and the
    /// three large humanoids must not share the humanoid eye. This is the pin
    /// that fails if the per-heritage table is dropped back to one constant.
    /// </summary>
    [Theory]
    [InlineData(ChargenHeritageGroup.Gearknight)]
    [InlineData(ChargenHeritageGroup.Tumerok)]
    [InlineData(ChargenHeritageGroup.Lugian)]
    [InlineData(ChargenHeritageGroup.Empyrean)]
    [InlineData(ChargenHeritageGroup.Olthoi)]
    [InlineData(ChargenHeritageGroup.OlthoiAcid)]
    public void LargeBodies_AreViewedFromFurtherBackThanAHuman(
        ChargenHeritageGroup heritage)
    {
        Vector3 eye = PaperdollHeritagePresentation.ResolveEye((uint)heritage);
        Assert.True(
            eye.Y < PaperdollHeritagePresentation.DefaultEye.Y,
            $"heritage {heritage} is framed at the humanoid distance {eye.Y}.");
    }

    [Fact]
    public void DollCamera_FollowsTheHeritageItIsSetTo()
    {
        var camera = new DollCamera();
        Assert.Equal(PaperdollHeritagePresentation.DefaultEye, camera.Eye);

        camera.SetHeritage((uint)ChargenHeritageGroup.Olthoi);
        Assert.Equal(new Vector3(0.12f, -3.4f, 0.88f), camera.Eye);
        Assert.True(Matrix4x4.Invert(camera.View, out Matrix4x4 inverse));
        Assert.Equal(-3.4f, inverse.Translation.Y, 3);

        camera.SetHeritage((uint)ChargenHeritageGroup.Aluvian);
        Assert.Equal(PaperdollHeritagePresentation.DefaultEye, camera.Eye);
    }
}
