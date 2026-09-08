using System;
using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanActiveDeviceProbeTests
{
    private static byte[] Filled(byte r, byte g, byte b, byte a)
    {
        int pixels = (int)(VulkanActiveDeviceProbe.ProbeExtent * VulkanActiveDeviceProbe.ProbeExtent);
        var buffer = new byte[pixels * 4];
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = r;
            buffer[i + 1] = g;
            buffer[i + 2] = b;
            buffer[i + 3] = a;
        }

        return buffer;
    }

    private static byte[] Expected() => Filled(
        VulkanActiveDeviceProbe.ExpectedClearRgba[0],
        VulkanActiveDeviceProbe.ExpectedClearRgba[1],
        VulkanActiveDeviceProbe.ExpectedClearRgba[2],
        VulkanActiveDeviceProbe.ExpectedClearRgba[3]);

    [Fact]
    public void TheProbeColourCannotBeMatchedByZeroedOrSaturatedMemory()
    {
        ReadOnlySpan<byte> colour = VulkanActiveDeviceProbe.ExpectedClearRgba;

        Assert.Equal(4, colour.Length);
        foreach (byte channel in colour)
        {
            Assert.NotEqual(0, channel);
            Assert.NotEqual(255, channel);
        }

        Assert.Throws<InvalidOperationException>(
            () => VulkanActiveDeviceProbe.VerifyClearColour(Filled(0, 0, 0, 0)));
        Assert.Throws<InvalidOperationException>(
            () => VulkanActiveDeviceProbe.VerifyClearColour(Filled(255, 255, 255, 255)));
    }

    [Fact]
    public void TheExpectedClearColourIsAccepted()
    {
        VulkanActiveDeviceProbe.VerifyClearColour(Expected());
    }

    [Fact]
    public void OneBitOfQuantisationSlackIsAllowedButTwoIsNot()
    {
        byte[] colour = VulkanActiveDeviceProbe.ExpectedClearRgba.ToArray();

        VulkanActiveDeviceProbe.VerifyClearColour(
            Filled((byte)(colour[0] + 1), colour[1], colour[2], colour[3]));
        VulkanActiveDeviceProbe.VerifyClearColour(
            Filled((byte)(colour[0] - 1), colour[1], colour[2], colour[3]));

        Assert.Throws<InvalidOperationException>(
            () => VulkanActiveDeviceProbe.VerifyClearColour(
                Filled((byte)(colour[0] + 2), colour[1], colour[2], colour[3])));
    }

    [Fact]
    public void ASinglyWrongPixelAnywhereIsRejected()
    {
        byte[] pixels = Expected();
        int lastPixel = pixels.Length - 4;
        pixels[lastPixel + 1] ^= 0xFF;

        Assert.Throws<InvalidOperationException>(
            () => VulkanActiveDeviceProbe.VerifyClearColour(pixels));
    }

    [Fact]
    public void AShortReadbackIsRejectedRatherThanPartiallyChecked()
    {
        Assert.Throws<InvalidOperationException>(
            () => VulkanActiveDeviceProbe.VerifyClearColour(new byte[64]));
    }
}
