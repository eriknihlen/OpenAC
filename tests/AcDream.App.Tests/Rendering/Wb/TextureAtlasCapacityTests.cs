using AcDream.App.Rendering.Wb;
using AcDream.Content;
using Chorizite.Core.Render.Enums;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class TextureAtlasCapacityTests
{
    [Theory]
    [InlineData(128, 128, 32)]
    [InlineData(256, 256, 24)]
    [InlineData(512, 512, 6)]
    [InlineData(1024, 1024, 1)]
    public void RgbaCapacityTargetsEightMiBIncludingMipChain(
        int width,
        int height,
        int expected)
    {
        Assert.Equal(
            expected,
            TextureAtlasManager.CalculateInitialCapacity(width, height, TextureFormat.RGBA8));
    }

    [Fact]
    public void SmallCompressedLayersRemainCountCapped()
    {
        Assert.Equal(
            TextureAtlasManager.MaximumArrayLayers,
            TextureAtlasManager.CalculateInitialCapacity(32, 32, TextureFormat.DXT1));
    }

    [Fact]
    public void DirectUploadAcceptsExactRgbaPayload()
    {
        RhiWorldTextureArray.ValidateUploadPayload(
            TextureFormat.RGBA8, 2, 2, 16, UploadPixelFormat.Rgba, UploadPixelType.UnsignedByte);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void DirectUploadRejectsNonExactPayloadLength(int bytes)
    {
        Assert.Throws<ArgumentException>(() =>
            RhiWorldTextureArray.ValidateUploadPayload(
                TextureFormat.RGBA8, 2, 2, bytes, UploadPixelFormat.Rgba, UploadPixelType.UnsignedByte));
    }

    [Fact]
    public void DirectUploadRejectsMismatchedTransferTuple()
    {
        Assert.Throws<ArgumentException>(() =>
            RhiWorldTextureArray.ValidateUploadPayload(
                TextureFormat.RGBA8, 2, 2, 16, UploadPixelFormat.Rgb, UploadPixelType.UnsignedByte));
        Assert.Throws<ArgumentException>(() =>
            RhiWorldTextureArray.ValidateUploadPayload(
                TextureFormat.RGBA8, 2, 2, 16, UploadPixelFormat.Rgba, UploadPixelType.Float));
    }

    [Fact]
    public void CompressedUploadRejectsUncompressedDescriptorOverride()
    {
        int bytes = RhiWorldTextureArray.CalculateExpectedDataSize(TextureFormat.DXT1, 4, 4);
        Assert.Throws<ArgumentException>(() =>
            RhiWorldTextureArray.ValidateUploadPayload(
                TextureFormat.DXT1, 4, 4, bytes, UploadPixelFormat.Rgba, UploadPixelType.UnsignedByte));
    }

    [Fact]
    public void RgbPayloadUsesTightlyPackedRows()
    {
        Assert.Equal(18, RhiWorldTextureArray.CalculateExpectedDataSize(TextureFormat.RGB8, 3, 2));
        RhiWorldTextureArray.ValidateUploadPayload(
            TextureFormat.RGB8, 3, 2, 18, UploadPixelFormat.Rgb, UploadPixelType.UnsignedByte);
    }
}
