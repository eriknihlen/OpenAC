using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Updates;
using AcDream.Tests.Fixtures.PluginIcons;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class LauncherPluginIconTests
{
    [Fact]
    public void ValidIconPasses()
    {
        LauncherPluginIcon.Validate(PngTestData.Valid());
    }

    [Fact]
    public void RejectsMoreThanMaximumBytes()
    {
        Assert.Throws<LauncherUpdateException>(
            () => LauncherPluginIcon.Validate(PngTestData.OverMaximumBytes()));
    }

    [Fact]
    public void RejectsMissingSignature()
    {
        Assert.Throws<LauncherUpdateException>(
            () => LauncherPluginIcon.Validate(PngTestData.NoSignature()));
    }

    [Fact]
    public void RejectsFirstChunkNotIhdr()
    {
        Assert.Throws<LauncherUpdateException>(
            () => LauncherPluginIcon.Validate(PngTestData.FirstChunkWrongType()));
    }

    [Fact]
    public void RejectsIhdrWithTheWrongLength()
    {
        Assert.Throws<LauncherUpdateException>(
            () => LauncherPluginIcon.Validate(PngTestData.FirstChunkWrongLength()));
    }

    [Fact]
    public void RejectsWrongDimensions()
    {
        Assert.Throws<LauncherUpdateException>(
            () => LauncherPluginIcon.Validate(PngTestData.WrongDimensions()));
    }

    [Fact]
    public void RejectsAChunkLengthAboveTheSignedIntMaximum()
    {
        Assert.Throws<LauncherUpdateException>(
            () => LauncherPluginIcon.Validate(PngTestData.ChunkLengthOverflow()));
    }

    [Fact]
    public void RejectsAChunkThatRunsPastTheEnd()
    {
        Assert.Throws<LauncherUpdateException>(
            () => LauncherPluginIcon.Validate(PngTestData.ChunkRunsPastEnd()));
    }

    [Fact]
    public void RejectsAnAnimationControlChunk()
    {
        Assert.Throws<LauncherUpdateException>(
            () => LauncherPluginIcon.Validate(PngTestData.AnimatedControl()));
    }

    [Fact]
    public void RejectsAMissingIendChunk()
    {
        Assert.Throws<LauncherUpdateException>(
            () => LauncherPluginIcon.Validate(PngTestData.MissingIend()));
    }

    [Fact]
    public void RejectsBytesAfterIend()
    {
        Assert.Throws<LauncherUpdateException>(
            () => LauncherPluginIcon.Validate(PngTestData.TrailingBytesAfterIend()));
    }

    [Fact]
    public void RejectsAnInflateBomb()
    {
        Assert.Throws<LauncherUpdateException>(
            () => LauncherPluginIcon.Validate(PngTestData.InflateBomb()));
    }

    [Theory]
    [InlineData(255, 6, 0, 0, 0)]
    [InlineData(16, 3, 0, 0, 0)]
    [InlineData(1, 2, 0, 0, 0)]
    [InlineData(8, 5, 0, 0, 0)]
    [InlineData(8, 6, 1, 0, 0)]
    [InlineData(8, 6, 0, 1, 0)]
    [InlineData(8, 6, 0, 0, 2)]
    public void RejectsAHeaderOutsideThePngSpecification(
        byte bitDepth, byte colorType, byte compression, byte filter, byte interlace)
    {
        Assert.Throws<LauncherUpdateException>(() => LauncherPluginIcon.Validate(
            PngTestData.WithHeader(bitDepth, colorType, 16, compression, filter, interlace)));
    }

    [Theory]
    [InlineData(1, 0, 64 * 9)]
    [InlineData(16, 6, 64 * 513)]
    [InlineData(8, 3, 64 * 65)]
    public void AcceptsEachAllowedDepthAtItsExactRawSize(byte bitDepth, byte colorType, int rawLength)
    {
        LauncherPluginIcon.Validate(PngTestData.WithHeader(bitDepth, colorType, rawLength));
    }

    // 64x64 RGBA8 with Adam7: the seven passes hold 264, 264, 520, 1040, 2064, 4128 and 8224
    // bytes including filter bytes, 16504 in all, where the same image without interlace is 16448.
    [Fact]
    public void AcceptsAnInterlacedIconAtItsExactRawSize()
    {
        LauncherPluginIcon.Validate(PngTestData.WithHeader(8, 6, 16504, interlace: 1));
    }

    [Fact]
    public void RejectsAnInterlacedIconOneByteOverItsRawSize()
    {
        Assert.Throws<LauncherUpdateException>(() => LauncherPluginIcon.Validate(
            PngTestData.WithHeader(8, 6, 16505, interlace: 1)));
    }
}
