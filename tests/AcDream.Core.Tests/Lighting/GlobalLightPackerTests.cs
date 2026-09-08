using System.Numerics;
using AcDream.Core.Lighting;
using Xunit;

namespace AcDream.Core.Tests.Lighting;

public class GlobalLightPackerTests
{
    [Fact]
    public void Pack_WritesSixteenFloatsPerLight_InTheExpectedLayout()
    {
        var light = new LightSource
        {
            Kind          = LightKind.Point,
            WorldPosition = new Vector3(10f, 20f, 30f),
            WorldForward  = new Vector3(0f, 0f, 1f),
            ColorLinear   = new Vector3(1.0f, 0.588f, 0.314f),
            Intensity     = 100f,
            Range         = 5.2f,
            ConeAngle     = 0f,
        };
        float[] buffer = System.Array.Empty<float>();

        int count = GlobalLightPacker.Pack(new[] { light }, ref buffer);

        Assert.Equal(1, count);
        Assert.True(buffer.Length >= 16);
        Assert.Equal(10f, buffer[0]); Assert.Equal(20f, buffer[1]); Assert.Equal(30f, buffer[2]);
        Assert.Equal((float)(int)LightKind.Point, buffer[3]);
        Assert.Equal(0f, buffer[4]); Assert.Equal(0f, buffer[5]); Assert.Equal(1f, buffer[6]);
        Assert.Equal(5.2f, buffer[7]);
        Assert.Equal(1.0f, buffer[8]); Assert.Equal(0.588f, buffer[9]); Assert.Equal(0.314f, buffer[10]);
        Assert.Equal(100f, buffer[11]);
        Assert.Equal(0f, buffer[12]);
    }

    [Fact]
    public void Pack_NullOrEmpty_ReturnsZero_AndBufferHasAtLeastOneSlot()
    {
        float[] buffer = System.Array.Empty<float>();
        int count = GlobalLightPacker.Pack(null, ref buffer);
        Assert.Equal(0, count);
        Assert.True(buffer.Length >= GlobalLightPacker.FloatsPerLight);
    }
}
