using AcDream.Core.Textures;
using Xunit;

namespace AcDream.Core.Tests.Textures;

public class SurfaceDecoderSolidColorTests
{
    [Fact]
    public void DecodeSolidColor_NullColor_ReturnsMagenta_DoesNotThrow()
    {
        var result = SurfaceDecoder.DecodeSolidColor(null!, 0f);
        Assert.Equal(DecodedTexture.Magenta, result);
    }
}
